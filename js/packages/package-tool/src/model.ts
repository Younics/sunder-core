import { readFile } from "node:fs/promises";
import { isAbsolute, relative, resolve } from "node:path";
import type { JsonValue, RpcContractDescriptor } from "@sunder/sdk";

export const SUPPORTED_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"] as const;
export type SunderRid = (typeof SUPPORTED_RIDS)[number];

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
  if (traversal === "" || traversal.startsWith("..") || isAbsolute(traversal)) {
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
  const id = text(config.id, "id", 256);
  if (!/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/u.test(id)) throw new Error("Package id must be lowercase dot-separated ASCII.");
  const name = text(config.name, "name", 256);
  const version = text(config.version, "version", 128);
  if (!semver(version)) throw new Error("Package version must be strict SemVer 2.0.");
  const contracts = array(config.contracts, "contracts").map((item, index) => {
    const contract = record(item, `contracts[${index}]`);
    only(contract, "path", "descriptorPath");
    return { path: text(contract.path, `contracts[${index}].path`, 240), descriptorPath: optionalText(contract.descriptorPath, `contracts[${index}].descriptorPath`, 200) };
  });
  const providers = array(config.providers, "providers").map((item, index) => {
    const provider = record(item, `providers[${index}]`);
    only(provider, "providerId", "contractId", "contractVersion");
    return {
      providerId: text(provider.providerId, `providers[${index}].providerId`, 256),
      contractId: text(provider.contractId, `providers[${index}].contractId`, 256),
      contractVersion: text(provider.contractVersion, `providers[${index}].contractVersion`, 128),
    };
  });
  const usesContracts = config.usesContracts === undefined ? [] : array(config.usesContracts, "usesContracts").map((item, index) => {
    const use = record(item, `usesContracts[${index}]`);
    only(use, "contractId", "versionRange", "required", "actions");
    const actions = array(use.actions, `usesContracts[${index}].actions`).map((action) => text(action, "action", 32));
    if (actions.some((action) => action !== "discover" && action !== "invoke" && action !== "subscribe")) throw new Error("Contract use actions are invalid.");
    return {
      contractId: text(use.contractId, `usesContracts[${index}].contractId`, 256),
      versionRange: text(use.versionRange, `usesContracts[${index}].versionRange`, 256),
      required: boolean(use.required, `usesContracts[${index}].required`),
      actions: actions as ContractUseConfig["actions"],
    };
  });
  const dependencies = config.dependencies === undefined ? [] : array(config.dependencies, "dependencies").map((item, index) => {
    const dependency = record(item, `dependencies[${index}]`);
    only(dependency, "packageId", "versionRange");
    return { packageId: text(dependency.packageId, `dependencies[${index}].packageId`, 256), versionRange: text(dependency.versionRange, `dependencies[${index}].versionRange`, 256) };
  });
  const result: SunderNodePackageConfig = {
    schemaVersion: 1,
    id,
    name,
    version,
    entry: text(config.entry, "entry", 240),
    contracts,
    providers,
    usesContracts,
    dependencies,
  };
  const app = config.app === undefined ? undefined : validateApp(config.app, id);
  return {
    ...result,
    ...(config.summary === undefined ? {} : { summary: text(config.summary, "summary", 2048) }),
    ...(config.outputDirectory === undefined ? {} : { outputDirectory: text(config.outputDirectory, "outputDirectory", 200) }),
    ...(config.maximumExecutableBytes === undefined ? {} : { maximumExecutableBytes: positiveInteger(config.maximumExecutableBytes, "maximumExecutableBytes") }),
    ...(config.maximumPackageBytes === undefined ? {} : { maximumPackageBytes: positiveInteger(config.maximumPackageBytes, "maximumPackageBytes") }),
    ...(app === undefined ? {} : { app }),
  };
}

function validateApp(value: unknown, packageId: string): AppConfig {
  const app = record(value, "app");
  only(app, "root", "entryPoint", "views");
  const root = app.root === undefined ? "." : text(app.root, "app.root", 200);
  const entryPoint = app.entryPoint === undefined ? "index.html" : logicalPath(app.entryPoint, "app.entryPoint");
  if (!entryPoint.endsWith(".html")) throw new Error("app.entryPoint must end with '.html'.");
  const views = array(app.views, "app.views").map((item, index) => {
    const view = record(item, `app.views[${index}]`);
    only(view, "viewId", "displayName", "route", "icon", "defaultPlacement", "showInHotbar");
    const viewId = text(view.viewId, `app.views[${index}].viewId`, 256);
    if (!/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/u.test(viewId) || !viewId.startsWith(`${packageId}.`)) {
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
      displayName: text(view.displayName, `app.views[${index}].displayName`, 128),
      route,
      ...(view.icon === undefined ? {} : { icon: logicalPath(view.icon, `app.views[${index}].icon`) }),
      defaultPlacement: placement as AppViewConfig["defaultPlacement"],
      showInHotbar: boolean(view.showInHotbar, `app.views[${index}].showInHotbar`),
    };
  });
  if (views.length === 0 || views.length > 32) throw new Error("app.views must contain between one and 32 views.");
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

function only(value: Record<string, unknown>, ...allowed: readonly string[]): void {
  const names = new Set(allowed);
  for (const name of Object.keys(value)) if (!names.has(name)) throw new Error(`Object contains unknown property '${name}'.`);
}

function text(value: unknown, label: string, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximum || value.trim() !== value) throw new Error(`${label} must be a non-empty bounded string.`);
  return value;
}

function optionalText(value: unknown, label: string, maximum: number): string | undefined {
  return value === undefined ? undefined : text(value, label, maximum);
}

function logicalPath(value: unknown, label: string): string {
  const path = text(value, label, 200);
  const segments = path.split("/");
  if (path.includes("\\") || path.startsWith("/") || path.endsWith("/") || segments.length > 24
    || segments.some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    throw new Error(`${label} must be a canonical package-relative path.`);
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

function semver(value: string): boolean {
  return /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/u.test(value);
}

export function asJsonValue(value: unknown): JsonValue {
  return value as JsonValue;
}
