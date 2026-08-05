import { readFile } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import {
  isArchiveRelativePath,
  isPackageId,
  isPackageVersionRange,
  isSemanticVersion,
  type JsonValue,
  type RpcContractDescriptor,
} from "@sunder/sdk";

export const SUPPORTED_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"] as const;
export type SunderRid = (typeof SUPPORTED_RIDS)[number];
export const MAXIMUM_COMPRESSED_PACKAGE_BYTES = 512 * 1024 * 1024;

const MAXIMUM_CONTRACTS = 128;
const MAXIMUM_CONTRACT_USES = 128;
const MAXIMUM_DEPENDENCIES = 128;
const MAXIMUM_PROVIDERS = 128;

export interface SunderNodePackageConfig {
  readonly schemaVersion: 1;
  readonly id: string;
  readonly name: string;
  readonly summary?: string;
  readonly version: string;
  readonly entry: string;
  readonly contracts: readonly ContractConfig[];
  readonly providers: readonly ProviderConfig[];
  readonly usesContracts?: readonly ContractUseConfig[];
  readonly dependencies?: readonly PackageDependencyConfig[];
  readonly outputDirectory?: string;
  readonly maximumExecutableBytes?: number;
  readonly maximumPackageBytes?: number;
  readonly app?: AppConfig;
}

export interface AppConfig {
  readonly root: string;
  readonly entryPoint: string;
  readonly views: readonly AppViewConfig[];
}

export interface AppViewConfig {
  readonly viewId: string;
  readonly displayName: string;
  readonly route: string;
  readonly icon?: string;
  readonly defaultPlacement: "leftTop" | "middle" | "rightTop" | "leftBottom" | "rightBottom";
  readonly showInHotbar: boolean;
}

export interface ContractConfig {
  readonly path: string;
  readonly descriptorPath?: string;
}

export interface ProviderConfig {
  readonly providerId: string;
  readonly contractId: string;
  readonly contractVersion: string;
}

export interface ContractUseConfig {
  readonly contractId: string;
  readonly versionRange: string;
  readonly required: boolean;
  readonly actions: readonly ("discover" | "invoke" | "subscribe")[];
}

export interface PackageDependencyConfig {
  readonly packageId: string;
  readonly versionRange: string;
}

export interface LoadedContract {
  readonly configuredPath: string;
  readonly sourcePath: string;
  readonly descriptorPath: string;
  readonly descriptor: RpcContractDescriptor;
  readonly canonical: string;
  readonly sha256: string;
}

export interface LoadedProject {
  readonly root: string;
  readonly configPath: string;
  readonly config: SunderNodePackageConfig;
  readonly outputRoot: string;
  readonly contracts: readonly LoadedContract[];
}

export async function loadProject(projectPath: string): Promise<LoadedProject> {
  const root = resolve(projectPath);
  const configPath = resolve(root, "sunder.package.json");
  const value = JSON.parse(await readFile(configPath, "utf8")) as unknown;
  const config = validateConfig(value);
  const outputRoot = resolveInside(root, config.outputDirectory ?? "dist", "outputDirectory");
  return { root, configPath, config, outputRoot, contracts: [] };
}

export function resolveInside(root: string, path: string, label: string): string {
  if (path.length === 0 || isAbsolute(path)) throw new Error(`${label} must be a non-empty project-relative path.`);
  const result = resolve(root, path);
  const traversal = relative(root, result);
  if (traversal === "" || traversal === ".." || traversal.startsWith(`..${sep}`) || isAbsolute(traversal)) {
    throw new Error(`${label} must resolve to a child of the package project.`);
  }
  return result;
}

export function currentRid(): SunderRid {
  const rid = `${process.platform === "win32" ? "win" : process.platform === "darwin" ? "osx" : process.platform}-${process.arch === "x64" ? "x64" : process.arch === "arm64" ? "arm64" : process.arch}`;
  if (!SUPPORTED_RIDS.includes(rid as SunderRid)) throw new Error(`Current platform '${rid}' is not one of the six supported exact Sunder RIDs.`);
  return rid as SunderRid;
}

function validateConfig(value: unknown): SunderNodePackageConfig {
  const config = record(value, "sunder.package.json");
  const allowed = new Set(["schemaVersion", "id", "name", "summary", "version", "entry", "contracts", "providers", "usesContracts", "dependencies", "outputDirectory", "maximumExecutableBytes", "maximumPackageBytes", "app"]);
  for (const key of Object.keys(config)) if (!allowed.has(key)) throw new Error(`sunder.package.json contains unknown property '${key}'.`);
  if (config.schemaVersion !== 1) throw new Error("sunder.package.json schemaVersion must be 1.");
  const id = text(config.id, "id", 128);
  if (!isPackageId(id)) throw new Error("Package id must be lowercase dot-separated ASCII.");
  const name = displayText(config.name, "name", 256);
  const version = text(config.version, "version", 256);
  if (!isSemanticVersion(version)) throw new Error("Package version must be strict SemVer 2.0.");
  const contracts = boundedArray(config.contracts, "contracts", MAXIMUM_CONTRACTS).map((item, index) => {
    const contract = record(item, `contracts[${index}]`);
    only(contract, "path", "descriptorPath");
    return {
      path: projectPath(contract.path, `contracts[${index}].path`, 240, 32),
      descriptorPath: contract.descriptorPath === undefined
        ? undefined
        : logicalPath(contract.descriptorPath, `contracts[${index}].descriptorPath`),
    };
  });
  const providers = boundedArray(config.providers, "providers", MAXIMUM_PROVIDERS).map((item, index) => {
    const provider = record(item, `providers[${index}]`);
    only(provider, "providerId", "contractId", "contractVersion");
    const providerId = text(provider.providerId, `providers[${index}].providerId`, 128);
    const contractId = text(provider.contractId, `providers[${index}].contractId`, 128);
    const contractVersion = text(provider.contractVersion, `providers[${index}].contractVersion`, 256);
    if (!isPackageId(providerId)) throw new Error(`providers[${index}].providerId must be a canonical package id.`);
    if (!isPackageId(contractId)) throw new Error(`providers[${index}].contractId must be a canonical package id.`);
    if (!isSemanticVersion(contractVersion)) throw new Error(`providers[${index}].contractVersion must be strict SemVer 2.0.`);
    return {
      providerId,
      contractId,
      contractVersion,
    };
  });
  const usesContracts = config.usesContracts === undefined ? [] : boundedArray(config.usesContracts, "usesContracts", MAXIMUM_CONTRACT_USES).map((item, index) => {
    const use = record(item, `usesContracts[${index}]`);
    only(use, "contractId", "versionRange", "required", "actions");
    const contractId = text(use.contractId, `usesContracts[${index}].contractId`, 128);
    const versionRange = text(use.versionRange, `usesContracts[${index}].versionRange`, 1024);
    if (!isPackageId(contractId)) throw new Error(`usesContracts[${index}].contractId must be a canonical package id.`);
    if (!isPackageVersionRange(versionRange)) throw new Error(`usesContracts[${index}].versionRange is unsupported.`);
    const actions = boundedArray(use.actions, `usesContracts[${index}].actions`, 3).map((action) => text(action, "action", 32));
    if (actions.length === 0 || new Set(actions).size !== actions.length) throw new Error("Contract use actions must be non-empty and unique.");
    if (actions.some((action) => action !== "discover" && action !== "invoke" && action !== "subscribe")) throw new Error("Contract use actions are invalid.");
    return {
      contractId,
      versionRange,
      required: boolean(use.required, `usesContracts[${index}].required`),
      actions: actions as ContractUseConfig["actions"],
    };
  });
  const dependencies = config.dependencies === undefined ? [] : boundedArray(config.dependencies, "dependencies", MAXIMUM_DEPENDENCIES).map((item, index) => {
    const dependency = record(item, `dependencies[${index}]`);
    only(dependency, "packageId", "versionRange");
    const packageId = text(dependency.packageId, `dependencies[${index}].packageId`, 128);
    const versionRange = text(dependency.versionRange, `dependencies[${index}].versionRange`, 1024);
    if (!isPackageId(packageId)) throw new Error(`dependencies[${index}].packageId must be a canonical package id.`);
    if (!isPackageVersionRange(versionRange)) throw new Error(`dependencies[${index}].versionRange is unsupported.`);
    return { packageId, versionRange };
  });
  requireUnique(contracts.map((contract) => contract.path), "Contract path");
  requireUnique(providers.map((provider) => provider.providerId), "Provider id");
  requireUnique(usesContracts.map((use) => use.contractId), "Contract use id");
  requireUnique(dependencies.map((dependency) => dependency.packageId), "Dependency package id");
  const result: SunderNodePackageConfig = {
    schemaVersion: 1,
    id,
    name,
    version,
    entry: projectPath(config.entry, "entry", 240, 32),
    contracts,
    providers,
    usesContracts,
    dependencies,
  };
  const app = config.app === undefined ? undefined : validateApp(config.app, id);
  return {
    ...result,
    ...(config.summary === undefined ? {} : { summary: displayText(config.summary, "summary", 2048) }),
    ...(config.outputDirectory === undefined ? {} : { outputDirectory: projectPath(config.outputDirectory, "outputDirectory", 200, 24) }),
    ...(config.maximumExecutableBytes === undefined ? {} : { maximumExecutableBytes: positiveInteger(config.maximumExecutableBytes, "maximumExecutableBytes") }),
    ...(config.maximumPackageBytes === undefined ? {} : { maximumPackageBytes: boundedPositiveInteger(config.maximumPackageBytes, "maximumPackageBytes", MAXIMUM_COMPRESSED_PACKAGE_BYTES) }),
    ...(app === undefined ? {} : { app }),
  };
}

function validateApp(value: unknown, packageId: string): AppConfig {
  const app = record(value, "app");
  only(app, "root", "entryPoint", "views");
  const root = app.root === undefined || app.root === "." ? "." : projectPath(app.root, "app.root", 200, 24);
  const entryPoint = app.entryPoint === undefined ? "index.html" : logicalPath(app.entryPoint, "app.entryPoint");
  if (!entryPoint.endsWith(".html")) throw new Error("app.entryPoint must end with '.html'.");
  const views = boundedArray(app.views, "app.views", 32).map((item, index) => {
    const view = record(item, `app.views[${index}]`);
    only(view, "viewId", "displayName", "route", "icon", "defaultPlacement", "showInHotbar");
    const viewId = text(view.viewId, `app.views[${index}].viewId`, 128);
    if (!isPackageId(viewId) || !viewId.startsWith(`${packageId}.`)) {
      throw new Error(`app.views[${index}].viewId must be a lowercase package id namespaced below '${packageId}'.`);
    }
    const route = text(view.route, `app.views[${index}].route`, 200);
    if (!webRoute(route)) throw new Error(`app.views[${index}].route is not canonical.`);
    const placement = text(view.defaultPlacement, `app.views[${index}].defaultPlacement`, 32);
    if (!["leftTop", "middle", "rightTop", "leftBottom", "rightBottom"].includes(placement)) {
      throw new Error(`app.views[${index}].defaultPlacement is invalid.`);
    }
    return {
      viewId,
      displayName: displayText(view.displayName, `app.views[${index}].displayName`, 128),
      route,
      ...(view.icon === undefined ? {} : { icon: logicalPath(view.icon, `app.views[${index}].icon`) }),
      defaultPlacement: placement as AppViewConfig["defaultPlacement"],
      showInHotbar: boolean(view.showInHotbar, `app.views[${index}].showInHotbar`),
    };
  });
  if (views.length === 0) throw new Error("app.views must contain between one and 32 views.");
  const ids = new Set<string>();
  const routes = new Set<string>();
  for (const view of views) {
    const foldedId = view.viewId.toLowerCase();
    const foldedRoute = view.route.toLowerCase();
    if (ids.has(foldedId)) throw new Error(`App view id '${view.viewId}' is duplicate or case-colliding.`);
    if (routes.has(foldedRoute)) throw new Error(`App view route '${view.route}' is duplicate or case-colliding.`);
    ids.add(foldedId);
    routes.add(foldedRoute);
  }
  return { root, entryPoint, views };
}

function record(value: unknown, label: string): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error(`${label} must be an object.`);
  return value as Record<string, unknown>;
}

function array(value: unknown, label: string): unknown[] {
  if (!Array.isArray(value)) throw new Error(`${label} must be an array.`);
  return value;
}

function boundedArray(value: unknown, label: string, maximum: number): unknown[] {
  const values = array(value, label);
  if (values.length > maximum) throw new Error(`${label} may contain at most ${maximum} entries.`);
  return values;
}

function only(value: Record<string, unknown>, ...allowed: readonly string[]): void {
  const names = new Set(allowed);
  for (const name of Object.keys(value)) if (!names.has(name)) throw new Error(`Object contains unknown property '${name}'.`);
}

function text(value: unknown, label: string, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximum || value.trim() !== value) throw new Error(`${label} must be a non-empty bounded string.`);
  return value;
}

function displayText(value: unknown, label: string, maximum: number): string {
  const result = text(value, label, maximum);
  if (/[\u0000-\u001f\u007f-\u009f]/u.test(result)) throw new Error(`${label} must not contain control characters.`);
  return result;
}

function logicalPath(value: unknown, label: string): string {
  const path = text(value, label, 200);
  if (!isArchiveRelativePath(path, 200, 24)) {
    throw new Error(`${label} must be a canonical package-relative path.`);
  }
  return path;
}

function projectPath(value: unknown, label: string, maximumLength: number, maximumDepth: number): string {
  const path = text(value, label, maximumLength);
  if (!isArchiveRelativePath(path, maximumLength, maximumDepth)) {
    throw new Error(`${label} must be a portable project-relative path.`);
  }
  return path;
}

function webRoute(value: string): boolean {
  if (value === "/") return true;
  if (!value.startsWith("/") || value.endsWith("/") || value.includes("\\") || value.includes("?") || value.includes("#") || value.includes("%")) return false;
  return value.slice(1).split("/").every((segment) => segment.length > 0 && segment !== "." && segment !== ".." && /^[A-Za-z0-9._~-]+$/u.test(segment));
}

function boolean(value: unknown, label: string): boolean {
  if (typeof value !== "boolean") throw new Error(`${label} must be boolean.`);
  return value;
}

function positiveInteger(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value <= 0) throw new Error(`${label} must be a positive safe integer.`);
  return value;
}

function boundedPositiveInteger(value: unknown, label: string, maximum: number): number {
  const result = positiveInteger(value, label);
  if (result > maximum) throw new Error(`${label} must not exceed ${maximum}.`);
  return result;
}

function requireUnique(values: readonly string[], label: string): void {
  const seen = new Set<string>();
  for (const value of values) {
    if (!seen.add(value)) throw new Error(`${label} '${value}' is declared more than once.`);
  }
}

export function asJsonValue(value: unknown): JsonValue {
  return value as JsonValue;
}
