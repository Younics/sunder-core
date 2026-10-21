import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { run } from "./check-supported-releases.mjs";
import { canonical, checkSupportedReleases, compareReleaseHistory, manifestPath, parseManifest, repositoryRoot, validateManifest } from "./supported-release-baselines.mjs";
import { qualifySdkRelease, sdkPackageNames } from "./sdk-release-qualification.mjs";

const unreleased = { schemaVersion: 1, baselineId: "sunder-v1", status: "UNFROZEN", releaseStatus: "UNRELEASED", releases: [] };
// All identities and bytes below are synthetic tests. They are not release evidence.
const source = { repository: "https://example.invalid/synthetic", commit: "a".repeat(40) };
const oldApi = "# sunder-public-api-v2\nassembly Synthetic, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null\ntype public class Sample.Api\n  method public System.Int32 Read()\n";

function fixture(context) {
  const root = mkdtempSync(resolve(tmpdir(), "sunder-compatibility-"));
  context.after(() => rmSync(root, { recursive: true, force: true }));
  const put = (path, text) => {
    mkdirSync(dirname(resolve(root, path)), { recursive: true });
    writeFileSync(resolve(root, path), text);
    return { path, sha256: createHash("sha256").update(text).digest("hex"), size: Buffer.byteLength(text) };
  };
  const retained = (path, text) => put(`eng/compatibility/releases/synthetic-1.1.0/${path}`, text);
  const artifacts = ["nuget", "source", "package", "host"].map(kind => ({ id: kind, kind, ...retained(`${kind}.bin`, `synthetic-${kind}`), source,
    retainedIdentity: `https://example.invalid/immutable-assets/${kind}/123` }));
  const currentPath = "editable/Synthetic.txt";
  put(currentPath, oldApi);
  const manifest = { ...unreleased, status: "FROZEN", releaseStatus: "RELEASED", releases: [{
    id: "synthetic-1.1.0", version: "1.1.0", source, artifacts,
    surfaces: [{ id: "Synthetic", format: "dotnet-public-api-v2", artifactId: "nuget", snapshot: retained("Synthetic.txt", oldApi), currentPath }],
    corpus: ["old-package-new-host", "old-source-new-sdk", "new-feature-old-host"].map((kind, index) => ({ id: kind, kind, owner: "synthetic-fixture",
      artifactIds: [["package"], ["source", "nuget"], ["host"]][index], recipe: retained(`${kind}.md`, `Synthetic ${kind} recipe; no released artifacts.`) })),
  }] };
  put(manifestPath, JSON.stringify(manifest));
  const check = (current = manifest, base = structuredClone(manifest), environment = {}) => checkSupportedReleases({ current, base, currentRoot: root, environment });
  return { root, manifest, put, check, currentPath };
}

test("checked-in V1 status explicitly claims neither freeze nor released artifacts", () => {
  const actual = parseManifest(readFileSync(resolve(repositoryRoot, manifestPath)));
  // Once a real release is retained, this case checks consistency rather than forbidding freeze.
  if (actual.status === "UNFROZEN") assert.deepEqual(actual, unreleased);
  else assert.ok(actual.releases.length > 0);
  compareReleaseHistory(unreleased, null);
});

test("retained release gate accepts additive API with independent authenticated snapshots", context => {
  const f = fixture(context);
  f.put(f.currentPath, oldApi + "  method public System.Void Added()\n");
  assert.equal(f.check().surfaces, 1);
  assert.equal(f.check().releaseStatus, "RELEASED");
});

test("editing code and current snapshot together still fails an unchanged released reference", context => {
  const f = fixture(context);
  f.put(f.currentPath, oldApi.replace("Read()", "Renamed()"));
  assert.throws(() => f.check(), /Released member removed or changed/u);
});

test("editing retained bytes or artifact hashes cannot rewrite externally anchored history", context => {
  const f = fixture(context);
  const base = structuredClone(f.manifest);
  const surface = f.manifest.releases[0].surfaces[0];
  f.put(surface.snapshot.path, oldApi.replace("Read()", "Gone()"));
  assert.throws(() => f.check(), /SHA-256 mismatch/u);
  surface.snapshot = f.put(surface.snapshot.path, oldApi.replace("Read()", "Gone()"));
  f.put(f.currentPath, oldApi.replace("Read()", "Gone()"));
  assert.throws(() => f.check(f.manifest, base), /Immutable released reference changed/u);
  const changed = structuredClone(base);
  changed.releases[0].artifacts[0].sha256 = "b".repeat(64);
  assert.throws(() => compareReleaseHistory(changed, base), /Immutable released reference changed/u);
});

test("reset removal and unanchored initial freeze fail while append-only history is possible", context => {
  const { manifest } = fixture(context);
  assert.throws(() => compareReleaseHistory(unreleased, manifest), /cannot be reset/u);
  assert.throws(() => compareReleaseHistory(manifest, null), /explicit pre-freeze base/u);
  compareReleaseHistory(manifest, unreleased);
  const appended = structuredClone(manifest);
  const second = JSON.parse(JSON.stringify(manifest.releases[0]).replaceAll("synthetic-1.1.0", "synthetic-1.1.1"));
  second.version = "1.1.1";
  appended.releases.push(second);
  compareReleaseHistory(appended, manifest);
  const removed = structuredClone(appended);
  removed.releases.shift();
  assert.throws(() => compareReleaseHistory(removed, appended), /Released reference removed/u);
});

test("frozen and CI checks reject all snapshot-update escape variables", context => {
  const f = fixture(context);
  for (const name of ["SUNDER_UPDATE_PUBLIC_API_ROOT", "SUNDER_UPDATE_PUBLIC_API_ASSEMBLIES", "SUNDER_UPDATE_PUBLIC_API_FIDELITY_ROOT"]) {
    assert.throws(() => f.check(f.manifest, f.manifest, { [name]: "update" }), /cannot bypass/u);
    assert.throws(() => checkSupportedReleases({ current: unreleased, base: null, currentRoot: f.root, environment: { CI: "true", [name]: "update" } }), /cannot bypass/u);
  }
});

test("manifest bounds unknown properties missing corpus and ambiguous identities fail closed", context => {
  const { manifest } = fixture(context);
  for (const mutate of [
    value => { value.extra = true; },
    value => { value.releases[0].surfaces[0].format = "ignore"; },
    value => { value.releases[0].source.commit = "0".repeat(40); },
    value => { value.releases[0].source.repository = "https://user:secret@example.invalid/repo"; },
    value => { value.releases[0].artifacts[0].size = 8589934593; },
    value => { value.releases[0].corpus.pop(); },
    value => { value.releases[0].corpus[0].artifactIds = ["source"]; },
    value => { value.releases[0].surfaces[0].artifactId = "missing"; },
    value => { value.releases[0].surfaces.push(value.releases[0].surfaces[0]); },
    value => { value.releases = Array(65).fill(value.releases[0]); },
    value => { value.releases[0].surfaces[0].currentPath = "../escape"; },
    value => { value.releases[0].surfaces[0].snapshot.path = "editable/Synthetic.txt"; },
    value => { value.releases[0].surfaces[0].currentPath = value.releases[0].surfaces[0].snapshot.path; },
  ]) {
    const copy = structuredClone(manifest);
    mutate(copy);
    assert.throws(() => validateManifest(copy));
  }
  assert.throws(() => parseManifest(" ".repeat(1024 * 1024 + 1)), /bound/u);
});

test("missing retained artifact and symbolic input redirection fail before surface success", context => {
  const f = fixture(context);
  const artifact = f.manifest.releases[0].artifacts[0];
  rmSync(resolve(f.root, artifact.path));
  assert.throws(() => f.check(), /ENOENT/u);
  f.put(artifact.path, "synthetic-nuget");
  rmSync(resolve(f.root, "editable"), { recursive: true });
  symlinkSync(dirname(resolve(f.root, artifact.path)), resolve(f.root, "editable"), process.platform === "win32" ? "junction" : "dir");
  assert.throws(() => f.check(), /Symlink/u);
});

test("exact-byte surfaces fail conservatively rather than inferring compatibility from arbitrary text", context => {
  const f = fixture(context);
  f.manifest.releases[0].surfaces[0].format = "exact-bytes-v1";
  f.check();
  f.put(f.currentPath, oldApi + "new text\n");
  assert.throws(() => f.check(), /exact-byte surface changed/u);
});

test("CLI requires an independent external anchor and does not swallow invalid Git base errors", context => {
  const f = fixture(context);
  assert.throws(() => run(["--root", f.root], {}), /Exactly one/u);
  assert.throws(() => run(["--root", f.root, "--base-ref", "main"], {}), /full nonzero/u);
  assert.throws(() => run(["--root", f.root, "--base-manifest", resolve(f.root, manifestPath)], {}), /independent/u);
  const external = f.put("external-base.json", JSON.stringify(unreleased));
  assert.equal(run(["--root", f.root, "--base-manifest", resolve(f.root, external.path)], {}).releases, 1);
  // No commits or config changes: an empty tree is not a commit and must not be accepted as a base.
  execFileSync("git", ["init", "--quiet", f.root]);
  assert.throws(() => run(["--root", f.root, "--base-ref", "f".repeat(40)], {}));
  const emptyTree = execFileSync("git", ["mktree"], { cwd: f.root, input: "", encoding: "utf8" }).trim();
  assert.throws(() => run(["--root", f.root, "--base-ref", emptyTree], {}), /commit object/u);
});

function qualificationFixture(context) {
  const f = fixture(context);
  const reference = f.manifest.releases[0];
  reference.source = { repository: "https://github.com/Younics/sunder-core", commit: source.commit };
  for (const artifact of reference.artifacts) artifact.source = reference.source;
  const retain = (name, text) => f.put(`eng/compatibility/releases/${reference.id}/${name}`, text);
  const packages = sdkPackageNames(reference.version).map((assetName, index) => {
    const artifactId = index === 0 ? "nuget" : `sdk-${index}`;
    const bytes = `synthetic-tested-${assetName}`;
    const artifact = { id: artifactId, kind: assetName.endsWith(".tgz") ? "npm" : "nuget", ...retain(assetName, bytes),
      source: reference.source, retainedIdentity: `https://example.invalid/synthetic-retention/${index + 1}` };
    if (index === 0) reference.artifacts[0] = artifact;
    else reference.artifacts.push(artifact);
    f.put(`packages/${assetName}`, bytes);
    return { artifactId, assetName };
  });
  const currentBytes = Buffer.from(JSON.stringify(unreleased));
  f.put(manifestPath, currentBytes);
  f.put("supported-releases.json", currentBytes);
  f.put("base-supported-releases.json", currentBytes);
  const hash = value => createHash("sha256").update(value).digest("hex");
  const executionRoot = mkdtempSync(resolve(tmpdir(), "sunder-independent-lanes-"));
  context.after(() => rmSync(executionRoot, { recursive: true, force: true }));
  const options = { currentBytes, baseBytes: currentBytes, currentRoot: f.root, qualificationRoot: f.root, candidateRoot: resolve(f.root, "packages"),
    executionRoot, version: reference.version, sourceCommit: source.commit, baseCommit: "b".repeat(40), runId: "123", runAttempt: "2", environment: {} };
  const receipt = { schemaVersion: 2, status: "freeze-qualified", repository: "Younics/sunder-core", sdkVersion: reference.version,
    artifactSourceCommit: source.commit, baseCommit: options.baseCommit, baseManifestSha256: hash(currentBytes), supportedManifestSha256: hash(currentBytes),
    invocation: { workflowPath: ".github/workflows/sunder-sdk-release.yml", workflowRef: "refs/tags/sdk/v1.1.0", workflowSha: source.commit, runId: "123", runAttempt: "1" },
    reference, packages };
  const collect = () => {
    rmSync(executionRoot, { recursive: true, force: true });
    f.put("qualification.json", canonical(receipt) + "\n");
    // Real bounded collector/owner subprocesses over synthetic inputs. The helper
    // explicitly mocks only Git provenance/native identity; never production evidence.
    execFileSync("python3", ["-B", resolve(repositoryRoot, "scripts/test/release/sdk_compatibility_fixture.py"), f.root, executionRoot], { stdio: ["ignore", "pipe", "pipe"] });
    Object.assign(receipt, JSON.parse(readFileSync(resolve(f.root, "qualification.json"))));
    options.currentBytes = readFileSync(resolve(f.root, "supported-releases.json"));
    options.baseBytes = readFileSync(resolve(f.root, "base-supported-releases.json"));
  };
  collect();
  const check = () => { f.put("qualification.json", canonical(receipt) + "\n"); return qualifySdkRelease(options); };
  const history = () => {
    const old = JSON.parse(JSON.stringify(receipt.reference).replaceAll("synthetic-1.1.0", "synthetic-1.0.0"));
    old.version = "1.0.0";
    for (const record of [...old.artifacts, ...old.surfaces.map(item => item.snapshot), ...old.corpus.map(item => item.recipe)]) {
      f.put(record.path, readFileSync(resolve(f.root, record.path.replaceAll("synthetic-1.0.0", "synthetic-1.1.0"))));
    }
    const ledger = { ...unreleased, status: "FROZEN", releaseStatus: "RELEASED", releases: [old] };
    options.currentBytes = options.baseBytes = Buffer.from(JSON.stringify(ledger));
    receipt.supportedManifestSha256 = receipt.baseManifestSha256 = hash(options.currentBytes);
    f.put(manifestPath, options.currentBytes);
    f.put("supported-releases.json", options.currentBytes);
    f.put("base-supported-releases.json", options.baseBytes);
    collect();
    return parseManifest(options.currentBytes);
  };
  return { ...f, receipt, options, check, history, hash };
}

test("release-only gate blocks empty dev success; first genuine tested candidate needs no published release", context => {
  const f = qualificationFixture(context);
  assert.equal(checkSupportedReleases({ current: unreleased, base: unreleased, currentRoot: f.root, environment: {} }).releases, 0);
  rmSync(resolve(f.root, "qualification.json"));
  assert.throws(() => qualifySdkRelease(f.options), /qualification missing/u);
  const qualified = f.check();
  assert.equal(qualified.status, "freeze-qualified");
  assert.equal(qualified.releaseStatus, "UNRELEASED");
  assert.equal(qualified.results, 13);
  assert.deepEqual(parseManifest(readFileSync(resolve(f.root, manifestPath))), unreleased);
  assert.throws(() => qualifySdkRelease({ ...f.options, baseBytes: null }), /pre-freeze base/u);
  assert.throws(() => qualifySdkRelease({ ...f.options, executionRoot: undefined }), /independent owning-lane/u);
});

test("qualification binds tag source exact tested package bytes all SDK packages and independent history", context => {
  const f = qualificationFixture(context);
  f.check();
  for (const mutate of [
    value => { value.status = "published"; },
    value => { value.sdkVersion = "1.1.1"; },
    value => { value.artifactSourceCommit = "c".repeat(40); },
    value => { value.reference.source.commit = "c".repeat(40); },
    value => { value.baseCommit = source.commit; },
    value => { value.baseManifestSha256 = "0".repeat(64); },
    value => { value.invocation.runId = "999"; },
    value => { value.invocation.runAttempt = "3"; },
    value => { value.invocation.workflowPath = ".github/workflows/acceptance.yml"; },
    value => { value.packages.pop(); },
    value => { value.packages[1] = value.packages[0]; },
    value => { value.reference.artifacts[0].source.commit = "c".repeat(40); },
    value => { value.reference.artifacts[0].path = "../outside"; },
    value => { value.passed = true; },
  ]) {
    const candidate = structuredClone(f.receipt);
    mutate(candidate);
    f.put("qualification.json", JSON.stringify(candidate));
    assert.throws(() => qualifySdkRelease(f.options));
  }
  f.check();
  f.put(`packages/${f.receipt.packages[0].assetName}`, "repacked");
  assert.throws(() => f.check(), /Retained size mismatch/u);
});

test("candidate freeze requires retained own bytes and exact own API snapshot, not just a compatible old surface", context => {
  const f = qualificationFixture(context);
  f.put(f.currentPath, oldApi + "  method public System.Void Added()\n");
  assert.throws(() => f.check(), /exact-byte surface changed/u);
  f.put(f.currentPath, oldApi);
  const input = f.receipt.reference.artifacts[0];
  rmSync(resolve(f.root, input.path));
  assert.throws(() => f.check(), /ENOENT/u);
  symlinkSync(resolve(f.options.candidateRoot, f.receipt.packages[0].assetName), resolve(f.root, input.path));
  assert.throws(() => f.check(), /Symlink/u);
});

test("later release retains its own new reference and runs every old and new corpus without rewriting history", context => {
  const f = qualificationFixture(context);
  const ledger = f.history();
  assert.equal(f.check().results, 26);
  const path = resolve(f.options.executionRoot, "sunder-sdk-compatibility-lane-portable/lane.json");
  const lane = JSON.parse(readFileSync(path));
  lane.executions = lane.executions.filter(item => item.referenceId === f.receipt.reference.id);
  writeFileSync(path, JSON.stringify(lane));
  assert.throws(() => f.check(), /Missing independently collected corpus execution/u);
  f.receipt.reference = ledger.releases[0];
  assert.throws(() => f.check(), /version differs/u);
  f.options.version = f.receipt.sdkVersion = "1.0.0";
  f.receipt.invocation.workflowRef = "refs/tags/sdk/v1.0.0";
  assert.throws(() => f.check(), /NEW immutable reference/u);
});

test("candidate cannot edit old hashes even when rewriting the corresponding local history copy", context => {
  const f = qualificationFixture(context);
  const ledger = f.history();
  ledger.releases[0].artifacts[0].sha256 = "d".repeat(64);
  f.options.currentBytes = Buffer.from(JSON.stringify(ledger));
  f.receipt.supportedManifestSha256 = f.hash(f.options.currentBytes);
  f.put("supported-releases.json", f.options.currentBytes);
  assert.throws(() => f.check(), /history must equal the trusted base/u);
});

test("candidate cannot choose report selectors or relabel independent execution evidence", context => {
  const f = qualificationFixture(context);
  f.check();
  f.receipt.results = Array.from({ length: 13 }, () => ({ tests: ["unrelated.passing.test"], report: "unrelated.trx" }));
  assert.throws(() => f.check(), /Invalid supported-release manifest/u);
  delete f.receipt.results;
  const path = resolve(f.options.executionRoot, "sunder-sdk-compatibility-lane-win-x64/lane.json");
  const original = JSON.parse(readFileSync(path));
  for (const mutate of [
    value => { value.context.actualRid = "linux-x64"; },
    value => { value.context.runId = "999"; },
    value => { value.executions.pop(); },
    value => { value.executions[0] = value.executions[1]; },
    value => { value.executions[0].before.recipeSha256 = "e".repeat(64); },
    value => { value.executions[0].report.path = "../outside.xml"; },
  ]) {
    const value = structuredClone(original);
    mutate(value);
    writeFileSync(path, JSON.stringify(value));
    assert.throws(() => f.check());
  }
  writeFileSync(path, JSON.stringify(original));
  f.check();
});

test("closed bounded release JSON rejects duplicate/case-colliding keys and excessive nesting", context => {
  const f = qualificationFixture(context);
  f.check();
  for (const raw of ['{"status":"UNFROZEN","status":"FROZEN"}', '{"status":"UNFROZEN","STATUS":"FROZEN"}',
    '['.repeat(34) + '0' + ']'.repeat(34), Buffer.from([0xff])]) assert.throws(() => parseManifest(raw));
  f.put("qualification.json", JSON.stringify(f.receipt).replace('"schemaVersion":2', '"schemaVersion":2,"schemaVersion":2'));
  assert.throws(() => qualifySdkRelease(f.options), /Duplicate/u);
  f.put("qualification.json", " ".repeat(1024 * 1024 + 1));
  assert.throws(() => qualifySdkRelease(f.options), /oversized/u);
});

test("release CLI cannot silently fall back to development or accept its own history anchor", context => {
  const f = qualificationFixture(context);
  f.check();
  const args = ["--root", f.root, "--base-manifest", resolve(f.root, "base-supported-releases.json"),
    "--release-version", "1.1.0", "--artifact-source-commit", source.commit, "--history-base-commit", f.options.baseCommit,
    "--qualification-root", f.root, "--candidate-root", f.options.candidateRoot, "--execution-root", f.options.executionRoot, "--run-id", "123", "--run-attempt", "2"];
  assert.equal(run(args, {}).status, "freeze-qualified");
  assert.throws(() => run(args.slice(0, -2), {}), /qualification requires/u);
  assert.throws(() => run(args.filter((_, index) => index !== 4 && index !== 5), {}), /cannot fall back/u);
  assert.throws(() => qualifySdkRelease({ ...f.options, baseCommit: source.commit }), /precede/u);
});
