import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { canonical, checkReleaseReference, checkSupportedReleases, containedFile, maximumManifestBytes,
  parseBoundedJson, parseManifest, readBounded, repositoryRoot, validateSchema, verifyRetainedFile } from "./supported-release-baselines.mjs";

const schema = JSON.parse(readFileSync(resolve(repositoryRoot, "eng/compatibility/sdk-release-qualification.schema.json"), "utf8"));
export const qualificationFile = "qualification.json";
export const missingQualification = "SDK release qualification missing: require compatibility/qualification.json, independent base/current manifests, a new retained candidate reference, exact tested SDK packages, and passing retained-corpus TRX/JUnit results from this SDK tag run. UNFROZEN/UNRELEASED dev success is not release qualification.";
const digest = bytes => createHash("sha256").update(bytes).digest("hex");

export function sdkPackageNames(version) {
  return ["Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build", "Sunder.Package.Templates"]
    .map(name => `${name}.${version}.nupkg`)
    .concat(["sunder-sdk", "sunder-package-tool", "create-sunder-package"].map(name => `${name}-${version}.tgz`));
}

export function qualifySdkRelease({ currentBytes, baseBytes, currentRoot, qualificationRoot, candidateRoot, executionRoot,
  version, sourceCommit, baseCommit, runId, runAttempt, environment = process.env }) {
  const require = (condition, message) => { if (!condition) throw new Error(`SDK release qualification: ${message}`); };
  for (const commit of [sourceCommit, baseCommit]) require(/^[a-f0-9]{40}$/u.test(commit ?? "") && !/^0+$/u.test(commit), "explicit nonzero source and trusted base commits are required.");
  require(baseCommit !== sourceCommit, "trusted history base must precede the artifact source, not be the candidate itself.");
  for (const number of [runId, runAttempt]) require(/^[1-9][0-9]*$/u.test(number ?? "") && Number.isSafeInteger(Number(number)), "exact workflow run/attempt required.");
  require(baseBytes !== null, "an explicit pre-freeze base manifest is required even for the first release.");
  let bytes;
  try { bytes = readBounded(containedFile(qualificationRoot, qualificationFile), maximumManifestBytes); }
  catch (error) { if (error.code === "ENOENT") throw new Error(missingQualification); throw error; }
  const receipt = parseBoundedJson(bytes);
  validateSchema(receipt, schema, "$qualification", schema);
  require(receipt.sdkVersion === version && receipt.reference.version === version, "candidate SDK version differs from the release tag.");
  require(receipt.artifactSourceCommit === sourceCommit && receipt.reference.source.commit === sourceCommit
    && receipt.reference.source.repository === `https://github.com/${receipt.repository}`, "candidate artifact source differs.");
  require(receipt.baseCommit === baseCommit && receipt.baseManifestSha256 === digest(baseBytes)
    && receipt.supportedManifestSha256 === digest(currentBytes), "trusted history/source manifest binding differs.");
  require(receipt.invocation.workflowRef === `refs/tags/sdk/v${version}` && receipt.invocation.workflowSha === sourceCommit
    && receipt.invocation.runId === runId && Number.isSafeInteger(Number(receipt.invocation.runAttempt))
    && Number(receipt.invocation.runAttempt) <= Number(runAttempt), "qualification belongs to another workflow/source/run or a future attempt.");
  const current = parseManifest(currentBytes);
  const base = parseManifest(baseBytes);
  require(current.releases.length < 64, "supported reference capacity is exhausted; cannot retain another release.");
  // No previous-only success and no same-version rewrite, even on a later patch.
  for (const ledger of [base, current]) require(!ledger.releases.some(item => item.id === receipt.reference.id || item.version === version), "candidate version needs a NEW immutable reference, absent from supported history.");
  require(canonical(current) === canonical(base), "supported history must equal the trusted base; confirm earlier publication separately before qualifying another release.");
  const files = new Set([qualificationFile, "supported-releases.json", "base-supported-releases.json"]);
  for (const [path, expected] of [["supported-releases.json", currentBytes], ["base-supported-releases.json", baseBytes]]) {
    require(readBounded(containedFile(qualificationRoot, path), maximumManifestBytes).equals(Buffer.from(expected)), `retained ${path} differs from its independent anchor.`);
  }
  checkSupportedReleases({ current, base, currentRoot, retainedRoot: qualificationRoot, environment: { ...environment, CI: "true" } });
  checkReleaseReference(receipt.reference, currentRoot, qualificationRoot, true);
  const references = [...current.releases, receipt.reference];
  for (const reference of references) {
    for (const artifact of reference.artifacts) files.add(artifact.path);
    for (const surface of reference.surfaces) files.add(surface.snapshot.path);
    for (const corpus of reference.corpus) files.add(corpus.recipe.path);
  }
  // A new reference must retain every previously supported surface selection too.
  for (const old of current.releases.flatMap(item => item.surfaces)) require(receipt.reference.surfaces.some(surface => surface.id === old.id
    && surface.currentPath === old.currentPath && surface.format === old.format), `candidate reference omits supported surface ${old.id}.`);
  const expected = sdkPackageNames(version).sort();
  require(canonical(receipt.packages.map(item => item.assetName).sort()) === canonical(expected)
    && new Set(receipt.packages.map(item => item.artifactId)).size === expected.length, "exact six NuGet/three npm candidate inventory required.");
  for (const item of receipt.packages) {
    const artifact = receipt.reference.artifacts.find(value => value.id === item.artifactId);
    require(artifact && canonical(artifact.source) === canonical(receipt.reference.source)
      && artifact.kind === (item.assetName.endsWith(".tgz") ? "npm" : "nuget"), `missing source-bound candidate package ${item.assetName}.`);
    verifyRetainedFile(candidateRoot, { ...artifact, path: item.assetName });
  }
  require(executionRoot, "missing independent owning-lane execution evidence; candidate-selected tests/reports are not qualification.");
  // The owning collector, not the candidate bundle, chooses commands from reviewed
  // immutable recipes, observes native execution, and records pre/post input hashes.
  // Publication replays the same evidence from the stage-pinned retention archive.
  let executions;
  try {
    executions = parseBoundedJson(execFileSync("python3", ["-B", resolve(repositoryRoot, "scripts/compatibility/sdk_compatibility_execution.py"),
      "verify", "--source-root", currentRoot, "--bundle-root", qualificationRoot, "--candidate-root", candidateRoot,
      "--execution-root", executionRoot, "--version", version, "--source", sourceCommit, "--run-id", runId, "--run-attempt", runAttempt],
    { encoding: "utf8", maxBuffer: maximumManifestBytes, timeout: 600000, stdio: ["ignore", "pipe", "pipe"] }));
  } catch (error) { throw new Error(`SDK owning-lane qualification: ${error.stderr?.toString().trim() || error.message}`); }
  return { status: "freeze-qualified", releaseStatus: "UNRELEASED", version, sourceCommit, referenceId: receipt.reference.id,
    references: references.length, results: executions.results, files: [...files].sort(), executionFiles: executions.files };
}
