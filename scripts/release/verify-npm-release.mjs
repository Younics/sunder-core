#!/usr/bin/env node

import { createHash, X509Certificate } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import { execFileSync } from "node:child_process";

const [version, npmVersionRange, npmTag, artifactArgument, repository, workflowPath, commit] = process.argv.slice(2);
if ([version, npmVersionRange, npmTag, artifactArgument, repository, workflowPath, commit].some((value) => value === undefined)) {
  throw new Error("Usage: verify-npm-release.mjs <version> <npm-version-range> <npm-tag> <artifact-directory> <owner/repo> <workflow-path> <commit>");
}
if (!/^[0-9a-f]{40}$/.test(commit)) {
  throw new Error(`Expected a lowercase 40-character Git commit, received '${commit}'.`);
}
const versionMatch = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.exec(version);
if (versionMatch === null) {
  throw new Error(`Expected a strict SemVer release version, received '${version}'.`);
}
const expectedNpmVersionRange = `>=${versionMatch[1]}.${versionMatch[2]}.0 <${versionMatch[1]}.${BigInt(versionMatch[2]) + 1n}.0`;
if (npmVersionRange !== expectedNpmVersionRange) {
  throw new Error(`Expected npm compatibility range '${expectedNpmVersionRange}', received '${npmVersionRange}'.`);
}

const npmVersion = execFileSync("npm", ["--version"], { encoding: "utf8" }).trim();
if (npmVersion !== "11.19.0") {
  throw new Error(`npm 11.19.0 is required, found ${npmVersion}.`);
}

const artifactDirectory = resolve(artifactArgument);
const registry = "https://registry.npmjs.org";
const packages = [
  { name: "@sunder/sdk", archive: `sunder-sdk-${version}.tgz` },
  { name: "@sunder/package-tool", archive: `sunder-package-tool-${version}.tgz` },
  { name: "create-sunder-package", archive: `create-sunder-package-${version}.tgz` },
];
const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};
const hash = (algorithm, bytes, encoding) => createHash(algorithm).update(bytes).digest(encoding);
const fetchJson = async (url) => {
  const response = await fetch(url, { headers: { accept: "application/json" } });
  if (!response.ok) throw new Error(`GET ${url} returned HTTP ${response.status}.`);
  return response.json();
};
const fetchBytes = async (url) => {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`GET ${url} returned HTTP ${response.status}.`);
  return Buffer.from(await response.arrayBuffer());
};

const packuments = new Map();
const localDigests = new Map();
for (const item of packages) {
  const localPath = resolve(artifactDirectory, item.archive);
  const localBytes = await readFile(localPath);
  const escapedName = encodeURIComponent(item.name);
  const metadata = await fetchJson(`${registry}/${escapedName}/${encodeURIComponent(version)}`);
  const packument = await fetchJson(`${registry}/${escapedName}`);
  assert(metadata.name === item.name && metadata.version === version, `npm metadata identity mismatch for ${item.name}@${version}.`);
  assert(metadata.license === "MIT", `${item.name}@${version} does not report the MIT license.`);
  assert(metadata.repository?.url === "git+https://github.com/Younics/sunder-core.git", `${item.name}@${version} has unexpected repository metadata.`);
  assert(packument["dist-tags"]?.[npmTag] === version, `${item.name}:${npmTag} does not resolve to ${version}.`);

  const integrity = `sha512-${hash("sha512", localBytes, "base64")}`;
  const shasum = hash("sha1", localBytes, "hex");
  assert(metadata.dist?.integrity === integrity, `${item.name}@${version} has unexpected registry integrity.`);
  assert(metadata.dist?.shasum === shasum, `${item.name}@${version} has unexpected registry shasum.`);
  assert(typeof metadata.dist?.tarball === "string", `${item.name}@${version} has no registry tarball URL.`);
  assert(metadata.dist?.attestations?.provenance?.predicateType === "https://slsa.dev/provenance/v1", `${item.name}@${version} has no SLSA provenance declaration.`);
  const remoteBytes = await fetchBytes(metadata.dist.tarball);
  assert(localBytes.equals(remoteBytes), `Registry bytes differ from ${basename(localPath)}.`);

  packuments.set(item.name, metadata);
  localDigests.set(item.name, hash("sha512", localBytes, "hex"));
}

assert(packuments.get("@sunder/package-tool")?.dependencies?.["@sunder/sdk"] === npmVersionRange, `Published @sunder/package-tool must use @sunder/sdk '${npmVersionRange}'.`);
assert(packuments.get("create-sunder-package")?.dependencies?.["@sunder/sdk"] === npmVersionRange, `Published create-sunder-package must use @sunder/sdk '${npmVersionRange}'.`);
assert(packuments.get("@sunder/package-tool")?.dependencies?.vite === "8.2.0", "Published @sunder/package-tool must pin Vite 8.2.0.");

const verificationDirectory = await mkdtemp(join(tmpdir(), "sunder-npm-release-verify-"));
try {
  await writeFile(join(verificationDirectory, "package.json"), `${JSON.stringify({ private: true }, null, 2)}\n`, "utf8");
  execFileSync(
    "npm",
    [
      "install",
      "--ignore-scripts",
      "--no-audit",
      "--no-fund",
      "--save-exact",
      ...packages.map((item) => `${item.name}@${version}`),
    ],
    { cwd: verificationDirectory, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  );
  execFileSync(
    "npm",
    ["ls", "--all"],
    { cwd: verificationDirectory, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  );
  const auditOutput = execFileSync(
    "npm",
    ["audit", "signatures", "--json", "--include-attestations"],
    { cwd: verificationDirectory, encoding: "utf8", maxBuffer: 128 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] },
  );
  const audit = JSON.parse(auditOutput);
  assert(Array.isArray(audit.invalid) && audit.invalid.length === 0, "npm found an invalid registry signature or attestation.");
  assert(Array.isArray(audit.missing) && audit.missing.length === 0, "npm found a missing registry signature.");
  assert(Array.isArray(audit.verified), "npm did not return verified attestation details.");

  const expectedRef = `refs/tags/sdk/v${version}`;
  const expectedRepositoryUrl = `https://github.com/${repository}`;
  const expectedWorkflowSan = `URI:${expectedRepositoryUrl}/${workflowPath}@${expectedRef}`;
  const expectedSourceUri = `git+${expectedRepositoryUrl}@${expectedRef}`;
  for (const item of packages) {
    const matches = audit.verified.filter((entry) => entry.name === item.name && entry.version === version);
    assert(matches.length === 1, `npm must verify exactly one attestation entry for ${item.name}@${version}.`);
    const provenanceBundles = matches[0].attestationBundles?.filter((entry) => entry.predicateType === "https://slsa.dev/provenance/v1") ?? [];
    assert(provenanceBundles.length === 1, `npm must verify exactly one SLSA provenance bundle for ${item.name}@${version}.`);

    const bundle = provenanceBundles[0].bundle;
    const statement = JSON.parse(Buffer.from(bundle.dsseEnvelope.payload, "base64").toString("utf8"));
    assert(statement._type === "https://in-toto.io/Statement/v1", `${item.name}@${version} has an unexpected provenance statement type.`);
    assert(statement.predicateType === "https://slsa.dev/provenance/v1", `${item.name}@${version} has an unexpected provenance predicate type.`);
    assert(Array.isArray(statement.subject) && statement.subject.length === 1, `${item.name}@${version} provenance must contain one subject.`);
    const purlName = item.name.startsWith("@") ? `%40${item.name.slice(1)}` : item.name;
    assert(statement.subject[0].name === `pkg:npm/${purlName}@${version}`, `${item.name}@${version} provenance subject name is unexpected.`);
    assert(statement.subject[0].digest?.sha512 === localDigests.get(item.name), `${item.name}@${version} provenance subject digest does not match the release tarball.`);

    const workflow = statement.predicate?.buildDefinition?.externalParameters?.workflow;
    assert(workflow?.repository === expectedRepositoryUrl, `${item.name}@${version} provenance repository is unexpected.`);
    assert(workflow?.path === workflowPath, `${item.name}@${version} provenance workflow path is unexpected.`);
    assert(workflow?.ref === expectedRef, `${item.name}@${version} provenance source ref is unexpected.`);
    const source = statement.predicate?.buildDefinition?.resolvedDependencies?.find((entry) => entry.uri === expectedSourceUri);
    assert(source?.digest?.gitCommit === commit, `${item.name}@${version} provenance commit does not match ${commit}.`);
    assert(statement.predicate?.runDetails?.builder?.id === "https://github.com/actions/runner/github-hosted", `${item.name}@${version} was not attested by a GitHub-hosted runner.`);

    const certificateBytes = Buffer.from(bundle.verificationMaterial?.certificate?.rawBytes ?? "", "base64");
    assert(certificateBytes.length > 0, `${item.name}@${version} provenance has no signing certificate.`);
    const certificate = new X509Certificate(certificateBytes);
    const sans = certificate.subjectAltName.split(/,\s*/);
    assert(sans.includes(expectedWorkflowSan), `${item.name}@${version} signing certificate does not bind ${expectedWorkflowSan}.`);
  }
} finally {
  await rm(verificationDirectory, { recursive: true, force: true });
}

console.log(`Verified npm bytes, digests, signatures, attestations, workflow, and commit for ${packages.length} packages.`);
