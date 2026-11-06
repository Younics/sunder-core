import { existsSync, readFileSync, realpathSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { isPackageVersionRange, isSemanticVersion, isVersionInRange } from "@younics/sunder-sdk";

export interface PackageToolCompatibility {
  readonly toolVersion: string;
  readonly sdkVersion: string;
  readonly sdkVersionRange: string;
  readonly sdkPackageRoot: string;
  readonly sdkPackageJson: string;
  readonly sdkMetadata: string;
  readonly modules: Readonly<Record<string, string>>;
  readonly declarations: Readonly<Record<string, string>>;
}

export function packageToolCompatibility(): PackageToolCompatibility {
  return projectSdkCompatibility(__dirname);
}

/** Resolve each project independently; dependency installation is an ordinary npm operation. */
export function projectSdkCompatibility(projectRoot: string): PackageToolCompatibility {
  const tool = readPackage(findPackageJson(__dirname, "@younics/sunder-package-tool"), "@younics/sunder-package-tool");
  const projectRequire = createRequire(resolve(projectRoot, "package.json"));
  // Inspect the current filesystem instead of Node's cached realpath resolution, so
  // replacing a pnpm/npm selection during watch cannot silently reuse its predecessor.
  const sdkPath = projectRequire.resolve.paths("@younics/sunder-sdk")?.map(path => resolve(path, "@younics/sunder-sdk/package.json")).find(existsSync);
  if (sdkPath === undefined) throw new Error(`Project '${projectRoot}' must install @younics/sunder-sdk before building.`);
  const sdk = readPackage(sdkPath, "@younics/sunder-sdk");
  const sdkPackageRoot = dirname(realpathSync(sdkPath));
  const sdkVersionRange = tool.dependencies?.["@younics/sunder-sdk"];
  if (typeof sdkVersionRange !== "string"
    || !isPackageVersionRange(sdkVersionRange)
    || !sdkVersionRange.includes("<")
    || !isVersionInRange(sdk.version, sdkVersionRange)) {
    throw new Error(`Project SDK '${sdk.version}' is outside this package tool's supported @younics/sunder-sdk range '${sdkVersionRange}'.`);
  }
  const modules: Record<string, string> = {}; const declarations: Record<string, string> = {};
  for (const name of ["", "/browser", "/core", "/generated", "/react"]) {
    const entry = sdk.exports?.[name === "" ? "." : "." + name];
    if (entry === undefined || typeof entry.require !== "string" || typeof entry.types !== "string")
      throw new Error(`Project SDK '${sdk.version}' does not expose the supported ${name || "."} authoring entry point.`);
    for (const [target, output] of [[entry.require, modules], [entry.types, declarations]] as const) {
      if (!target.startsWith("./") || target.includes("..") || target.includes("\\")) throw new Error("Invalid SDK export path.");
      output["@younics/sunder-sdk" + name] = resolve(sdkPackageRoot, target);
    }
  }
  return Object.freeze({
    toolVersion: tool.version,
    sdkVersion: sdk.version,
    sdkVersionRange,
    sdkPackageRoot,
    sdkPackageJson: sdkPath,
    sdkMetadata: readFileSync(sdkPath, "utf8"),
    modules: Object.freeze(modules), declarations: Object.freeze(declarations),
  });
}

export function revalidateProjectSdk(projectRoot: string, selected: PackageToolCompatibility): void {
  if (JSON.stringify(projectSdkCompatibility(projectRoot)) !== JSON.stringify(selected))
    throw new Error("The project's SDK selection changed during the build; retry with the installed dependencies.");
}

function findPackageJson(start: string, expectedName: string): string {
  let directory = resolve(start);
  while (true) {
    const candidate = resolve(directory, "package.json");
    try {
      const value = JSON.parse(readFileSync(candidate, "utf8")) as { readonly name?: unknown };
      if (value.name === expectedName) return candidate;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    const parent = dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
  throw new Error(`Could not locate released package metadata for '${expectedName}'.`);
}

function readPackage(path: string, expectedName: string): PackageMetadata {
  const value = JSON.parse(readFileSync(path, "utf8")) as Partial<PackageMetadata>;
  if (value.name !== expectedName || typeof value.version !== "string" || !isSemanticVersion(value.version)) {
    throw new Error(`Package metadata for '${expectedName}' does not contain an exact semantic version.`);
  }
  return value as PackageMetadata;
}

interface PackageMetadata {
  readonly name: string;
  readonly version: string;
  readonly dependencies?: Readonly<Record<string, string>>;
  readonly exports?: Readonly<Record<string, { readonly require?: string; readonly types?: string }>>;
}
