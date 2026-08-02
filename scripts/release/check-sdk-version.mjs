#!/usr/bin/env node

import { appendFile, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const expectedVersion = process.argv[2];
if (process.argv.length > 3) {
  throw new Error("Usage: check-sdk-version.mjs [expected-version]");
}

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const readText = (path) => readFile(resolve(root, path), "utf8");
const readJson = async (path) => JSON.parse(await readText(path));

const props = await readText("Directory.Build.props");
const property = (name) => {
  const matches = [...props.matchAll(new RegExp(`<${name}>([^<]+)</${name}>`, "g"))];
  if (matches.length !== 1) {
    throw new Error(`Directory.Build.props must define exactly one ${name}.`);
  }
  return matches[0][1].trim();
};

const version = property("SunderDeveloperPackageVersion");
const versionRange = property("SunderDeveloperPackageVersionRange");
const assemblyVersion = property("SunderSdkAssemblyVersion");
const semver = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;
const match = semver.exec(version);
if (match === null) {
  throw new Error(`SunderDeveloperPackageVersion '${version}' is not a strict SemVer release version.`);
}
if (match[4] !== undefined) {
  for (const identifier of match[4].split(".")) {
    if (/^[0-9]+$/.test(identifier) && identifier.length > 1 && identifier.startsWith("0")) {
      throw new Error(`Numeric SemVer prerelease identifier '${identifier}' contains a leading zero.`);
    }
  }
}
if (expectedVersion !== undefined && expectedVersion !== version) {
  throw new Error(`Release version '${expectedVersion}' does not match production package metadata '${version}'.`);
}

const major = match[1];
const minor = match[2];
const nextMinor = (BigInt(minor) + 1n).toString();
const computedRange = `[${major}.${minor}.0,${major}.${nextMinor}.0)`;
const npmVersionRange = `>=${major}.${minor}.0 <${major}.${nextMinor}.0`;
if (versionRange !== computedRange) {
  throw new Error(`SunderDeveloperPackageVersionRange must be '${computedRange}', not '${versionRange}'.`);
}
const computedAssemblyVersion = `${major}.${minor}.0.0`;
if (assemblyVersion !== computedAssemblyVersion) {
  throw new Error(`SunderSdkAssemblyVersion must be '${computedAssemblyVersion}', not '${assemblyVersion}'.`);
}

const packagePaths = [
  "js/package.json",
  "js/packages/sdk/package.json",
  "js/packages/package-tool/package.json",
  "js/packages/create-sunder-package/package.json",
];
const packages = new Map();
for (const path of packagePaths) {
  const manifest = await readJson(path);
  if (manifest.version !== version) {
    throw new Error(`${path} version '${manifest.version}' does not match '${version}'.`);
  }
  packages.set(path, manifest);
}

const packageTool = packages.get("js/packages/package-tool/package.json");
const createPackage = packages.get("js/packages/create-sunder-package/package.json");
const rootPackage = packages.get("js/package.json");
const packageManager = rootPackage.packageManager;
const packageManagerMatch = /^npm@([0-9]+\.[0-9]+\.[0-9]+)$/.exec(packageManager ?? "");
if (packageManagerMatch === null) {
  throw new Error("js/package.json must pin packageManager to an exact npm version.");
}
const npmVersion = packageManagerMatch[1];
if (npmVersion !== "11.19.0") {
  throw new Error(`js/package.json packageManager must be 'npm@11.19.0', not '${packageManager}'.`);
}
const viteVersion = rootPackage.devDependencies?.vite;
if (viteVersion !== "8.2.0") {
  throw new Error(`js/package.json must pin Vite to '8.2.0', not '${viteVersion}'.`);
}
if (packageTool.dependencies?.["@sunder/sdk"] !== npmVersionRange) {
  throw new Error(`@sunder/package-tool must depend on @sunder/sdk '${npmVersionRange}'.`);
}
if (createPackage.dependencies?.["@sunder/sdk"] !== npmVersionRange) {
  throw new Error(`create-sunder-package must depend on @sunder/sdk '${npmVersionRange}'.`);
}
if (packageTool.dependencies?.vite !== viteVersion) {
  throw new Error(`@sunder/package-tool must pin Vite to '${viteVersion}'.`);
}

const templates = new Map();
for (const templatePath of [
  "js/packages/create-sunder-package/template/package.json",
  "js/packages/create-sunder-package/template-react-node/package.json",
]) {
  const template = await readJson(templatePath);
  if (template.dependencies?.["@sunder/sdk"] !== npmVersionRange) {
    throw new Error(`${templatePath} must use @sunder/sdk '${npmVersionRange}'.`);
  }
  if (template.devDependencies?.["@sunder/package-tool"] !== npmVersionRange) {
    throw new Error(`${templatePath} must use @sunder/package-tool '${npmVersionRange}'.`);
  }
  templates.set(templatePath, template);
}
if (templates.get("js/packages/create-sunder-package/template-react-node/package.json")
  ?.devDependencies?.vite !== `^${viteVersion}`) {
  throw new Error(`The React/Node template must use Vite '^${viteVersion}'.`);
}

const lock = await readJson("js/package-lock.json");
const lockVersions = [
  ["js/package-lock.json", lock.version],
  ["js/package-lock.json packages['']", lock.packages?.[""]?.version],
  ["js/package-lock.json packages/sdk", lock.packages?.["packages/sdk"]?.version],
  ["js/package-lock.json packages/package-tool", lock.packages?.["packages/package-tool"]?.version],
  ["js/package-lock.json packages/create-sunder-package", lock.packages?.["packages/create-sunder-package"]?.version],
];
for (const [location, value] of lockVersions) {
  if (value !== version) {
    throw new Error(`${location} version '${value}' does not match '${version}'.`);
  }
}
if (lock.lockfileVersion !== 3) {
  throw new Error(`js/package-lock.json must use lockfileVersion 3, not '${lock.lockfileVersion}'.`);
}
if (lock.packages?.[""]?.devDependencies?.vite !== viteVersion
    || lock.packages?.["packages/package-tool"]?.dependencies?.vite !== viteVersion
    || lock.packages?.["node_modules/vite"]?.version !== viteVersion) {
  throw new Error(`js/package-lock.json does not consistently pin Vite '${viteVersion}'.`);
}
if (lock.packages?.["packages/package-tool"]?.dependencies?.["@sunder/sdk"] !== npmVersionRange
    || lock.packages?.["packages/create-sunder-package"]?.dependencies?.["@sunder/sdk"] !== npmVersionRange) {
  throw new Error(`js/package-lock.json does not contain internal npm range '${npmVersionRange}'.`);
}

const nugetProjects = [
  "src/Sdk/Sunder.Sdk/Sunder.Sdk.csproj",
  "src/Sdk/Sunder.Sdk.Avalonia/Sunder.Sdk.Avalonia.csproj",
  "src/Sdk/Sunder.Sdk.Stacks/Sunder.Sdk.Stacks.csproj",
  "src/Sdk/Sunder.Package.Build/Sunder.Package.Build.csproj",
  "src/Sdk/Sunder.Package.Templates/Sunder.Package.Templates.csproj",
];
for (const path of nugetProjects) {
  const project = await readText(path);
  if (!project.includes("<Version>$(SunderDeveloperPackageVersion)</Version>")) {
    throw new Error(`${path} must use SunderDeveloperPackageVersion as its package version source.`);
  }
  if (!project.includes("<AssemblyVersion>$(SunderSdkAssemblyVersion)</AssemblyVersion>")) {
    throw new Error(`${path} must use SunderSdkAssemblyVersion as its assembly version source.`);
  }
}

const output = { version, versionRange, npmVersionRange, assemblyVersion, npmVersion, viteVersion };
if (process.env.GITHUB_OUTPUT !== undefined) {
  await appendFile(
    process.env.GITHUB_OUTPUT,
    `version=${version}\nversion_range=${versionRange}\nnpm_version_range=${npmVersionRange}\nassembly_version=${assemblyVersion}\nnpm_version=${npmVersion}\nvite_version=${viteVersion}\n`,
    "utf8",
  );
}
console.log(JSON.stringify(output));
