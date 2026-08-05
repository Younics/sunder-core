import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { isPackageVersionRange, isSemanticVersion, isVersionInRange } from "@sunder/sdk";

export interface PackageToolCompatibility {
  readonly toolVersion: string;
  readonly sdkVersion: string;
  readonly sdkVersionRange: string;
  readonly sdkBaselineCapability: string;
  readonly sdkPackageRoot: string;
}

let loadedCompatibility: PackageToolCompatibility | undefined;

export function packageToolCompatibility(): PackageToolCompatibility {
  if (loadedCompatibility !== undefined) return loadedCompatibility;
  const tool = readPackage(findPackageJson(__dirname, "@sunder/package-tool"), "@sunder/package-tool");
  const sdkPath = findPackageJson(dirname(require.resolve("@sunder/sdk")), "@sunder/sdk");
  const sdk = readPackage(sdkPath, "@sunder/sdk");
  const sdkVersionRange = tool.dependencies?.["@sunder/sdk"];
  if (typeof sdkVersionRange !== "string"
    || !isPackageVersionRange(sdkVersionRange)
    || !sdkVersionRange.includes("<")
    || !isVersionInRange(sdk.version, sdkVersionRange)) {
    throw new Error("@sunder/package-tool must declare a bounded @sunder/sdk range containing the exact installed SDK version.");
  }
  const match = /^(\d+)\.(\d+)\./u.exec(sdk.version);
  if (match === null) throw new Error("The installed @sunder/sdk version cannot define a Host SDK baseline.");
  loadedCompatibility = Object.freeze({
    toolVersion: tool.version,
    sdkVersion: sdk.version,
    sdkVersionRange,
    sdkBaselineCapability: `sdk-baseline-${Number(match[1])}-${Number(match[2])}.v1`,
    sdkPackageRoot: dirname(sdkPath),
  });
  return loadedCompatibility;
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
}
