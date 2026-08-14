import { readFile } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import {
  isArchiveRelativePath,
  isPackageId,
  isPackageVersionRange,
  isSemanticVersion,
  type JsonValue,
  type RpcContractDescriptorV2,
} from "@sunder/sdk";

export const SUPPORTED_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"] as const;
export type SunderRid = (typeof SUPPORTED_RIDS)[number];
export const SUPPORTED_WORKER_CAPABILITIES = [
  "auth.v1",
  "callbacks.v1",
  "logging.v1",
  "role-local-workspace.v1",
  "secrets.v1",
  "settings.schema.v1",
  "settings.v1",
  "storage.key-migration.v1",
  "storage.v1",
] as const;
export type WorkerCapability = (typeof SUPPORTED_WORKER_CAPABILITIES)[number];
export const MAXIMUM_COMPRESSED_PACKAGE_BYTES = 512 * 1024 * 1024;
export type PackageRole = "app" | "runtime";
export type ContractExposure = "package" | "public";
export type ProviderAudience = "owner" | "public";

const RPC_BINDINGS_OUTPUT_PATH = ".sunder/generated/rpc";

const MAXIMUM_CONTRACTS = 128;
const MAXIMUM_CONTRACT_USES = 128;
const MAXIMUM_DEPENDENCIES = 128;
const MAXIMUM_PROVIDERS = 128;
const MAXIMUM_RPC_BINDINGS = 128;

export interface SunderNodePackageConfig {
  readonly schemaVersion: 2;
  readonly id: string;
  readonly name: string;
  readonly summary?: string;
  readonly version: string;
  readonly entry: string;
  readonly workerCapabilities: readonly WorkerCapability[];
  readonly contracts: readonly ContractConfig[];
  readonly providers: readonly ProviderConfig[];
  readonly usesContracts?: readonly ContractUseConfig[];
  readonly dependencies?: readonly PackageDependencyConfig[];
  readonly outputDirectory?: string;
  readonly maximumExecutableBytes?: number;
  readonly maximumPackageBytes?: number;
  readonly app?: AppConfig;
  readonly rpcBindings?: readonly RpcBindingConfig[];
}

export interface RpcBindingConfig {
  readonly path: string;
  readonly outputFile: string;
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
  readonly exposure: ContractExposure;
}

export interface ProviderConfig {
  readonly providerId: string;
  readonly role: PackageRole;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly audience: ProviderAudience;
}

export interface ContractUseConfig {
  readonly role: PackageRole;
  readonly contractId: string;
  readonly contractVersion: string;
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
  readonly descriptor: RpcContractDescriptorV2;
  readonly canonicalBytes: Buffer;
  readonly canonical: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
  readonly exposure: ContractExposure;
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
  const outputDirectory = config.outputDirectory ?? "dist";
  if (portablePathsContainOneAnother(outputDirectory, RPC_BINDINGS_OUTPUT_PATH)) {
    throw new Error(`outputDirectory '${outputDirectory}' must not equal, contain, or be contained by the owned RPC binding output '${RPC_BINDINGS_OUTPUT_PATH}'.`);
  }
  const outputRoot = resolveInside(root, outputDirectory, "outputDirectory");
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
  const allowed = new Set(["schemaVersion", "id", "name", "summary", "version", "entry", "workerCapabilities", "contracts", "providers", "usesContracts", "dependencies", "outputDirectory", "maximumExecutableBytes", "maximumPackageBytes", "app", "rpcBindings"]);
  for (const key of Object.keys(config)) if (!allowed.has(key)) throw new Error(`sunder.package.json contains unknown property '${key}'.`);
  if (config.schemaVersion === 1) {
    throw new Error("sunder.package.json schemaVersion 1 is no longer supported; migrate RPC package authoring to schemaVersion 2.");
  }
  if (config.schemaVersion !== 2) throw new Error("sunder.package.json schemaVersion must be exactly 2.");
  const id = text(config.id, "id", 128);
  if (!isPackageId(id)) throw new Error("Package id must be lowercase dot-separated ASCII.");
  const name = displayText(config.name, "name", 256);
  const version = text(config.version, "version", 256);
  if (!isSemanticVersion(version)) throw new Error("Package version must be strict SemVer 2.0.");
  const workerCapabilities = boundedArray(
    config.workerCapabilities,
    "workerCapabilities",
    SUPPORTED_WORKER_CAPABILITIES.length,
  ).map((item, index) => {
    const capability = text(item, `workerCapabilities[${index}]`, 128);
    if (!SUPPORTED_WORKER_CAPABILITIES.includes(capability as WorkerCapability)) {
      throw new Error(`workerCapabilities[${index}] '${capability}' is not implemented by the TypeScript Worker V3 SDK.`);
    }
    return capability as WorkerCapability;
  });
  requireUnique(workerCapabilities, "Worker capability");
  if (workerCapabilities.includes("auth.v1") && !workerCapabilities.includes("callbacks.v1")) {
    workerCapabilities.push("callbacks.v1");
  }
  if (workerCapabilities.includes("storage.key-migration.v1") && !workerCapabilities.includes("storage.v1")) {
    workerCapabilities.push("storage.v1");
  }
  workerCapabilities.sort();
  const contracts = boundedArray(config.contracts, "contracts", MAXIMUM_CONTRACTS).map((item, index) => {
    const contract = record(item, `contracts[${index}]`);
    only(contract, "path", "descriptorPath", "exposure");
    return {
      path: projectPath(contract.path, `contracts[${index}].path`, 240, 32),
      descriptorPath: contract.descriptorPath === undefined
        ? undefined
        : logicalPath(contract.descriptorPath, `contracts[${index}].descriptorPath`),
      exposure: oneOf(contract.exposure, `contracts[${index}].exposure`, "package", "public"),
    };
  });
  const providers = boundedArray(config.providers, "providers", MAXIMUM_PROVIDERS).map((item, index) => {
    const provider = record(item, `providers[${index}]`);
    only(provider, "providerId", "role", "contractId", "contractVersion", "audience");
    const providerId = text(provider.providerId, `providers[${index}].providerId`, 128);
    const contractId = text(provider.contractId, `providers[${index}].contractId`, 128);
    const contractVersion = text(provider.contractVersion, `providers[${index}].contractVersion`, 256);
    const role = oneOf(provider.role, `providers[${index}].role`, "app", "runtime");
    if (!isPackageId(providerId)) throw new Error(`providers[${index}].providerId must be a canonical package id.`);
    if (!isPackageId(contractId)) throw new Error(`providers[${index}].contractId must be a canonical package id.`);
    if (!isSemanticVersion(contractVersion)) throw new Error(`providers[${index}].contractVersion must be strict SemVer 2.0.`);
    if (role === "app") {
      throw new Error(`providers[${index}] declares role 'app'. App-role providers are not supported because App provider registration and serving are not available.`);
    }
    return {
      providerId,
      role,
      contractId,
      contractVersion,
      audience: oneOf(provider.audience, `providers[${index}].audience`, "owner", "public"),
    };
  });
  const usesContracts = config.usesContracts === undefined ? [] : boundedArray(config.usesContracts, "usesContracts", MAXIMUM_CONTRACT_USES).map((item, index) => {
    const use = record(item, `usesContracts[${index}]`);
    only(use, "role", "contractId", "contractVersion", "required", "actions");
    const contractId = text(use.contractId, `usesContracts[${index}].contractId`, 128);
    const contractVersion = text(use.contractVersion, `usesContracts[${index}].contractVersion`, 256);
    if (!isPackageId(contractId)) throw new Error(`usesContracts[${index}].contractId must be a canonical package id.`);
    if (!isSemanticVersion(contractVersion)) throw new Error(`usesContracts[${index}].contractVersion must be strict SemVer 2.0.`);
    const actions = boundedArray(use.actions, `usesContracts[${index}].actions`, 3).map((action) => text(action, "action", 32));
    if (actions.length === 0 || new Set(actions).size !== actions.length) throw new Error("Contract use actions must be non-empty and unique.");
    if (actions.some((action) => action !== "discover" && action !== "invoke" && action !== "subscribe")) throw new Error("Contract use actions are invalid.");
    return {
      role: oneOf(use.role, `usesContracts[${index}].role`, "app", "runtime"),
      contractId,
      contractVersion,
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
  const rpcBindings = config.rpcBindings === undefined ? undefined : boundedArray(config.rpcBindings, "rpcBindings", MAXIMUM_RPC_BINDINGS).map((item, index) => {
    const binding = record(item, `rpcBindings[${index}]`);
    only(binding, "path", "outputFile");
    return {
      path: projectPath(binding.path, `rpcBindings[${index}].path`, 240, 32),
      outputFile: rpcBindingOutputFile(binding.outputFile, `rpcBindings[${index}].outputFile`),
    };
  });
  requireUnique(contracts.map((contract) => contract.path), "Contract path");
  requireUnique(providers.map((provider) => `${provider.role}/${provider.providerId}`), "Provider role/id");
  requireUnique(usesContracts.map((use) => `${use.role}/${use.contractId}`), "Contract use role/id");
  requireUnique(dependencies.map((dependency) => dependency.packageId), "Dependency package id");
  requireUnique((rpcBindings ?? []).map((binding) => binding.path), "RPC binding input path");
  validateRpcBindingOutputPaths(rpcBindings ?? []);
  const result: SunderNodePackageConfig = {
    schemaVersion: 2,
    id,
    name,
    version,
    entry: projectPath(config.entry, "entry", 240, 32),
    workerCapabilities,
    contracts,
    providers,
    usesContracts,
    dependencies,
    ...(rpcBindings === undefined ? {} : { rpcBindings }),
  };
  const app = config.app === undefined ? undefined : validateApp(config.app, id);
  for (const use of usesContracts) {
    if (use.role === "app" && app === undefined) {
      throw new Error(`Contract use '${use.contractId}' declares role 'app', but the package has no App target.`);
    }
  }
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

function rpcBindingOutputFile(value: unknown, label: string): string {
  const path = projectPath(value, label, 240, 32);
  const fileName = path.split("/").at(-1)!;
  if (!fileName.endsWith(".ts") || fileName.length === ".ts".length || asciiCaseFold(fileName).endsWith(".d.ts")) {
    throw new Error(`${label} must identify a .ts file and must not be a .d.ts declaration file.`);
  }
  return path;
}

function validateRpcBindingOutputPaths(bindings: readonly RpcBindingConfig[]): void {
  const entries = new Map<string, { readonly path: string; readonly kind: "directory" | "file" }>();
  for (const binding of bindings) {
    const segments = binding.outputFile.split("/");
    for (let index = 1; index <= segments.length; index++) {
      const candidate = segments.slice(0, index).join("/");
      const kind = index === segments.length ? "file" : "directory";
      const folded = asciiCaseFold(candidate);
      const existing = entries.get(folded);
      if (existing !== undefined && (kind === "file" || existing.path !== candidate || existing.kind !== kind)) {
        throw new Error(`RPC binding output paths '${existing.path}' and '${candidate}' collide on portable filesystems.`);
      }
      entries.set(folded, { path: candidate, kind });
    }
  }
}

function asciiCaseFold(value: string): string {
  return value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 0x20));
}

function portablePathsContainOneAnother(left: string, right: string): boolean {
  const leftSegments = asciiCaseFold(left).split("/");
  const rightSegments = asciiCaseFold(right).split("/");
  const commonLength = Math.min(leftSegments.length, rightSegments.length);
  return leftSegments.slice(0, commonLength).every((segment, index) => segment === rightSegments[index]);
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

function oneOf<T extends string>(value: unknown, label: string, ...values: readonly T[]): T {
  if (typeof value !== "string" || !values.includes(value as T)) {
    throw new Error(`${label} must be one of ${values.map((item) => `'${item}'`).join(", ")}.`);
  }
  return value as T;
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
    if (seen.has(value)) throw new Error(`${label} '${value}' is declared more than once.`);
    seen.add(value);
  }
}

export function asJsonValue(value: unknown): JsonValue {
  return value as JsonValue;
}
