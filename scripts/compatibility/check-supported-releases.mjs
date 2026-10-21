import { execFileSync } from "node:child_process";
import { realpathSync, statSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { checkSupportedReleases, manifestPath, maximumManifestBytes, parseManifest, readBounded, repositoryRoot } from "./supported-release-baselines.mjs";
import { qualifySdkRelease } from "./sdk-release-qualification.mjs";

export function run(arguments_, environment = process.env) {
  const options = new Map();
  for (let index = 0; index < arguments_.length; index += 2) {
    const name = arguments_[index];
    if (!["--base-ref", "--base-manifest", "--root", "--retained-root", "--release-version", "--artifact-source-commit",
      "--history-base-commit", "--qualification-root", "--candidate-root", "--execution-root", "--run-id", "--run-attempt"].includes(name)
        || options.has(name) || !arguments_[index + 1] || arguments_[index + 1].startsWith("--")) {
      throw new Error("Usage: check-supported-releases.mjs (--base-ref <full-commit> | --base-manifest <external-file>) [--root <checkout>] [--retained-root <retained-bundle>]. Release mode additionally requires --release-version, --artifact-source-commit, --qualification-root, --candidate-root, --execution-root (independent owning-lane artifacts), --run-id, --run-attempt, and --history-base-commit when using --base-manifest.");
    }
    options.set(name, arguments_[index + 1]);
  }
  if (options.has("--base-ref") === options.has("--base-manifest")) throw new Error("Exactly one external/base manifest identity is required; current-only comparison is not a gate.");
  const root = resolve(options.get("--root") ?? repositoryRoot);
  const currentFile = resolve(root, manifestPath);
  const currentBytes = readBounded(currentFile, maximumManifestBytes);
  const current = parseManifest(currentBytes);
  let base;
  let baseBytes;
  if (options.has("--base-manifest")) {
    const basePath = resolve(options.get("--base-manifest"));
    const baseStat = statSync(basePath);
    const currentStat = statSync(currentFile);
    if (realpathSync(basePath) === realpathSync(currentFile)
        || (baseStat.dev === currentStat.dev && baseStat.ino === currentStat.ino)) throw new Error("Base manifest must be independent of the current manifest.");
    baseBytes = readBounded(basePath, maximumManifestBytes);
    base = parseManifest(baseBytes);
  } else {
    const commit = options.get("--base-ref");
    if (!/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/u.test(commit) || /^0+$/u.test(commit)) throw new Error("Base ref must be an explicit full nonzero Git commit identity.");
    const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8", maxBuffer: maximumManifestBytes + 1, stdio: ["ignore", "pipe", "pipe"] });
    if (git("cat-file", "-t", commit).trim() !== "commit") throw new Error("Base ref must identify a Git commit object.");
    // Distinguish a legitimately absent first-introduction manifest from a bad/missing Git object.
    const entry = git("ls-tree", commit, "--", manifestPath).trim();
    baseBytes = entry ? Buffer.from(git("show", `${commit}:${manifestPath}`)) : null;
    base = baseBytes === null ? null : parseManifest(baseBytes);
  }
  const releaseOptions = ["--artifact-source-commit", "--qualification-root", "--candidate-root", "--execution-root", "--run-id", "--run-attempt"];
  if (options.has("--release-version")) {
    if (releaseOptions.some(name => !options.has(name))) throw new Error(`Release qualification requires ${releaseOptions.join(", ")}.`);
    if (options.has("--retained-root") || (options.has("--history-base-commit") && options.has("--base-ref"))) throw new Error("Release mode uses --qualification-root and exactly one history commit coordinate.");
    return qualifySdkRelease({ currentBytes, baseBytes, currentRoot: root,
      qualificationRoot: resolve(options.get("--qualification-root")), candidateRoot: resolve(options.get("--candidate-root")),
      executionRoot: resolve(options.get("--execution-root")),
      version: options.get("--release-version"), sourceCommit: options.get("--artifact-source-commit"),
      baseCommit: options.get("--base-ref") ?? options.get("--history-base-commit"),
      runId: options.get("--run-id"), runAttempt: options.get("--run-attempt"), environment });
  }
  if ([...releaseOptions, "--history-base-commit"].some(name => options.has(name))) throw new Error("Release-only arguments require --release-version; they cannot fall back to the dev gate.");
  return checkSupportedReleases({ current, base, currentRoot: root, retainedRoot: resolve(options.get("--retained-root") ?? root), environment });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const result = run(process.argv.slice(2));
    if (result.status === "freeze-qualified") process.stdout.write(`${JSON.stringify(result)}\n`);
    else process.stdout.write(`Supported-release gate: ${result.status}/${result.releaseStatus}; releases=${result.releases}; surfaces=${result.surfaces}.\n`);
    if (result.status === "UNFROZEN") process.stdout.write("No released V1 artifact corpus is claimed; concrete freeze inputs remain required.\n");
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
