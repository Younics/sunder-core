#!/usr/bin/env node

import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { lstat, readFile, readdir } from "node:fs/promises";
import { basename, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const REPOSITORY_URL = "git+https://github.com/Younics/sunder-core.git";
const PRERELEASE_IDENTIFIER = "(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)";
const SEMVER = new RegExp(`^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-${PRERELEASE_IDENTIFIER}(?:\\.${PRERELEASE_IDENTIFIER})*)?$`, "u");

const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};
const digest = (algorithm, bytes, encoding) => createHash(algorithm).update(bytes).digest(encoding);
const readArchiveText = (archive, entry) => execFileSync(
  "tar",
  ["-xOf", archive, entry],
  { encoding: "utf8", maxBuffer: 16 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] },
);
const readArchiveJson = (archive, entry) => {
  try {
    return JSON.parse(readArchiveText(archive, entry));
  } catch (error) {
    throw new Error(`${basename(archive)} contains invalid or missing '${entry}'.`, { cause: error });
  }
};

export async function verifyNodePackageArtifacts({
  version,
  npmVersionRange,
  viteVersion,
  artifactDirectory,
  packReportPath,
}) {
  const versionMatch = SEMVER.exec(version);
  assert(versionMatch !== null, `Expected a strict SemVer release version, received '${version}'.`);
  const expectedRange = `>=${versionMatch[1]}.${versionMatch[2]}.0 <${versionMatch[1]}.${BigInt(versionMatch[2]) + 1n}.0`;
  assert(npmVersionRange === expectedRange, `Expected npm compatibility range '${expectedRange}', received '${npmVersionRange}'.`);
  assert(/^\d+\.\d+\.\d+$/u.test(viteVersion), `Expected an exact Vite version, received '${viteVersion}'.`);

  const directory = resolve(artifactDirectory);
  const directoryStat = await lstat(directory);
  assert(directoryStat.isDirectory() && !directoryStat.isSymbolicLink(), `npm artifact directory must be a real directory: ${directory}`);
  const packages = [
    { name: "@sunder/sdk", archive: `sunder-sdk-${version}.tgz` },
    { name: "@sunder/package-tool", archive: `sunder-package-tool-${version}.tgz` },
    { name: "create-sunder-package", archive: `create-sunder-package-${version}.tgz` },
  ];
  const expectedArchives = new Set(packages.map((item) => item.archive));
  const topLevelEntries = await readdir(directory, { withFileTypes: true });
  const actualArchives = topLevelEntries
    .filter((entry) => entry.name.endsWith(".tgz"))
    .map((entry) => entry.name)
    .sort();
  assert(
    actualArchives.length === expectedArchives.size
      && actualArchives.every((name) => expectedArchives.has(name)),
    `npm artifact paths do not match the coordinated set; found ${JSON.stringify(actualArchives)}.`,
  );

  let packReport;
  try {
    packReport = JSON.parse(await readFile(resolve(packReportPath), "utf8"));
  } catch (error) {
    throw new Error(`npm pack report is missing or invalid: ${packReportPath}`, { cause: error });
  }
  assert(Array.isArray(packReport) && packReport.length === packages.length, "npm pack must report exactly three package artifacts.");

  const results = [];
  const manifests = new Map();
  for (const item of packages) {
    const archivePath = resolve(directory, item.archive);
    const archiveStat = await lstat(archivePath);
    assert(archiveStat.isFile() && !archiveStat.isSymbolicLink(), `npm artifact must be a regular file: ${archivePath}`);
    const entries = execFileSync(
      "tar",
      ["-tzf", archivePath],
      { encoding: "utf8", maxBuffer: 32 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] },
    ).split("\n").filter(Boolean);
    assert(entries.length > 0, `${item.archive} is empty.`);
    assert(new Set(entries).size === entries.length, `${item.archive} contains duplicate archive paths.`);
    assert(entries.every((entry) => {
      const parts = entry.split("/");
      return entry.startsWith("package/")
        && !entry.includes("\\")
        && !parts.includes("..")
        && !entry.startsWith("/");
    }), `${item.archive} contains an unsafe or non-package archive path.`);
    for (const requiredEntry of ["package/package.json", "package/LICENSE", "package/NOTICE", "package/README.md"]) {
      assert(entries.filter((entry) => entry === requiredEntry).length === 1, `${item.archive} must contain exactly one '${requiredEntry}'.`);
    }

    const manifest = readArchiveJson(archivePath, "package/package.json");
    assert(manifest.name === item.name, `${item.archive} package identity is '${manifest.name}', expected '${item.name}'.`);
    assert(manifest.version === version, `${item.archive} package version is '${manifest.version}', expected '${version}'.`);
    assert(manifest.license === "MIT", `${item.archive} must declare the MIT license.`);
    assert(manifest.repository?.url === REPOSITORY_URL, `${item.archive} has an unexpected repository identity.`);
    assert(manifest.publishConfig?.access === "public", `${item.archive} must publish with public access.`);
    assert(manifest.publishConfig?.provenance === true, `${item.archive} must require npm provenance.`);
    manifests.set(item.name, manifest);

    const bytes = await readFile(archivePath);
    const integrity = `sha512-${digest("sha512", bytes, "base64")}`;
    const shasum = digest("sha1", bytes, "hex");
    const reportMatches = packReport.filter((entry) => entry?.name === item.name && entry?.version === version);
    assert(reportMatches.length === 1, `npm pack must report exactly one '${item.name}@${version}' artifact.`);
    const report = reportMatches[0];
    assert(report.filename === item.archive, `npm pack reported unexpected filename '${report.filename}' for ${item.name}.`);
    assert(report.size === bytes.length, `npm pack reported the wrong byte size for ${item.archive}.`);
    assert(report.integrity === integrity, `npm pack reported the wrong SHA-512 integrity for ${item.archive}.`);
    assert(report.shasum === shasum, `npm pack reported the wrong SHA-1 digest for ${item.archive}.`);
    results.push({
      name: item.name,
      version,
      path: item.archive,
      size: bytes.length,
      sha1: shasum,
      sha512: digest("sha512", bytes, "hex"),
      integrity,
    });
  }

  assert(manifests.get("@sunder/package-tool")?.dependencies?.["@sunder/sdk"] === npmVersionRange,
    `@sunder/package-tool must depend on @sunder/sdk '${npmVersionRange}'.`);
  assert(manifests.get("create-sunder-package")?.dependencies?.["@sunder/sdk"] === npmVersionRange,
    `create-sunder-package must depend on @sunder/sdk '${npmVersionRange}'.`);
  assert(manifests.get("@sunder/package-tool")?.dependencies?.vite === viteVersion,
    `@sunder/package-tool must pin Vite '${viteVersion}'.`);

  const createArchive = resolve(directory, `create-sunder-package-${version}.tgz`);
  for (const template of ["template", "template-react-node"]) {
    const manifest = readArchiveJson(createArchive, `package/${template}/package.json`);
    assert(manifest.dependencies?.["@sunder/sdk"] === npmVersionRange,
      `${template} must depend on @sunder/sdk '${npmVersionRange}'.`);
    assert(manifest.devDependencies?.["@sunder/package-tool"] === npmVersionRange,
      `${template} must depend on @sunder/package-tool '${npmVersionRange}'.`);
    if (template === "template-react-node") {
      assert(manifest.devDependencies?.vite === `^${viteVersion}`,
        `${template} must depend on Vite '^${viteVersion}'.`);
    }
  }

  return results;
}

async function main() {
  const [version, npmVersionRange, viteVersion, artifactDirectory, packReportPath] = process.argv.slice(2);
  if ([version, npmVersionRange, viteVersion, artifactDirectory, packReportPath].some((value) => value === undefined)
      || process.argv.length !== 7) {
    throw new Error("Usage: verify-node-package-artifacts.mjs <version> <npm-version-range> <vite-version> <artifact-directory> <npm-pack.json>");
  }
  const result = await verifyNodePackageArtifacts({ version, npmVersionRange, viteVersion, artifactDirectory, packReportPath });
  console.log(JSON.stringify({ packages: result }));
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  await main();
}
