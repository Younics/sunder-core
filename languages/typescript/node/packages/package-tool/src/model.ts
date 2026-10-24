import { lstat } from "node:fs/promises";
import { basename, isAbsolute, relative, resolve, sep } from "node:path";
import ts from "typescript";
import {
  isArchiveRelativePath,
  isPackageId,
  isSemanticVersion,
  type JsonValue,
  type RpcContractDescriptor,
} from "@sunder/sdk";
import { validateDependencyVersionRange } from "./dependency-range";
import { analyzeOwnedRpc, type OwnedRpcAnalysis, type OwnedAnalysisOptions } from "./rpc-authoring";
import { BASELINE_FILE, exists } from "./rpc-baseline-store";
import { readProjectDescriptorFile } from "./descriptor-files";

export const SUPPORTED_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"] as const;
export type SunderRid = (typeof SUPPORTED_RIDS)[number];
export const SUPPORTED_WORKER_CAPABILITIES = [
  "auth.v1",
  "callbacks.v1",
  "logging.v1",
  "role-local-workspace.v1",
  "rpc.call-ownership.v1",
  "rpc.extended-deadline.v1",
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
const MAXIMUM_NATIVE_ASSETS = 256;
const MAXIMUM_PROVIDERS = 128;
const MAXIMUM_RPC_BINDINGS = 128;

export type SunderNodePackageConfig =
  Omit<ResolvedNodePackageConfig, "schemaVersion" | "entry" | "services" | "contracts" | "providers" | "usesContracts" | "rpcBindings"> & {
    readonly schemaVersion: 3;
    readonly contracts?: never;
    readonly providers?: never;
    readonly usesContracts?: never;
    readonly rpcBindings?: never;
  } & ({ readonly services: { readonly module: string }; readonly entry?: never } | { readonly services?: never; readonly entry: string });

/** Validated build projection; V3 RPC declarations are derived, never authored. */
export interface ResolvedNodePackageConfig {
  readonly schemaVersion: 2 | 3;
  readonly services?: { readonly module: string };
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
  readonly maximumPackageBytes?: number;
  readonly app?: AppConfig;
  readonly rpcBindings?: readonly RpcBindingConfig[];
  readonly nativeAssets?: NativeAssetsConfig;
}

export type NativeAssetsConfig = Readonly<Partial<Record<SunderRid, readonly NativeAssetConfig[]>>>;

export interface NativeAssetConfig {
  readonly sourcePath: string;
  readonly targetPath: string;
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
  readonly modelProjection: import("./rpc-model-projection").RpcModelProjection;
  readonly methodPolicies?: import("./rpc-model-projection").RpcMethodPolicies;
  readonly naturalContent?:boolean;
  readonly appUse?: Readonly<{ required: boolean; actions: readonly ("discover" | "invoke" | "subscribe")[] }>;
  readonly runtimeUse?: Readonly<{ required: boolean; actions: readonly ("discover" | "invoke" | "subscribe")[] }>;
  readonly imported?: import("./rpc-contract-lock").ArtifactContract;
  readonly contractKey?: string;
  readonly configuredPath: string;
  readonly sourcePath: string;
  readonly descriptorPath: string;
  readonly descriptor: RpcContractDescriptor;
  readonly canonicalBytes: Buffer;
  readonly canonical: string;
  readonly descriptorSha256: string;
  readonly abiSha256: string;
  readonly exposure: ContractExposure;
}

export interface LoadedProject {
  readonly ownedRpc?: OwnedRpcAnalysis;
  readonly root: string;
  readonly configPath: string;
  readonly config: ResolvedNodePackageConfig;
  readonly outputRoot: string;
  readonly contracts: readonly LoadedContract[];
}

export async function loadProject(projectPath: string, options: import("./rpc-contract-lock").RegistryOriginOptions = {}): Promise<LoadedProject> {
  return loadProjectWithOwnedFrame(projectPath, options.allowDevelopmentRegistryOrigin === undefined ? {} : { allowDevelopmentRegistryOrigin: options.allowDevelopmentRegistryOrigin });
}

/** Tool-private acceptance frame; not exported by the package entry point. */
export async function loadProjectWithOwnedFrame(projectPath: string, analysisOptions: OwnedAnalysisOptions = {}): Promise<LoadedProject> {
  const { project, configBytes } = await loadProjectConfiguration(projectPath);
  const { root, config } = project;
  if (config.services === undefined) {
    for (const name of [BASELINE_FILE, `${BASELINE_FILE}.sunder-output.lock`, "sunder-contracts.lock.json",
      ".sunder/generated/rpc/authoring-receipt.v1.json", ".sunder/generated/rpc/package-export.v1.json", ".sunder/generated/rpc/owned-baseline.sha256"]) {
      if (await exists(resolve(root, name))) throw new Error("Existing V3 authoring source/baseline/lock evidence cannot be consumed through descriptor-first or non-RPC fallback.");
    }
    if (analysisOptions.requireBaseline || analysisOptions.allowDrift) throw new Error("contracts commands require a configured V3 owned RPC source module.");
    return project;
  }
  const ownedRpc = await analyzeOwnedRpc(project, configBytes, analysisOptions);
  const contracts=[...ownedRpc.contracts,...ownedRpc.imports.contracts];
  const workerCapabilities=[...new Set([...config.workerCapabilities,...(contracts.some(contract=>contract.methodPolicies!==undefined)?["rpc.extended-deadline.v1" as const]:[])])].sort();
  return { ...project, ownedRpc, contracts, config: { ...config, workerCapabilities, providers: ownedRpc.providers, usesContracts: [...ownedRpc.appUses, ...ownedRpc.runtimeUses, ...ownedRpc.imports.uses] } };
}

/** Tool-private configuration reader shared by initial dependency selection and source analysis. */
export async function loadProjectConfiguration(projectPath: string): Promise<{ project: LoadedProject; configBytes: Buffer }> {
  const root = resolve(projectPath);
  if (await exists(resolve(root, ".sunder-builder-metadata.transaction.json")))
    throw new Error("metadata.recovery: An interrupted metadata save has recovery data. Recover the source transaction before generation or build.");
  const configPath = resolve(root, "sunder.package.json");
  const configBytes = await readProjectDescriptorFile(root, configPath, "Package config");
  const configText = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(configBytes);
  const value = JSON.parse(configText) as unknown;
  const configTree = ts.parseJsonText(configPath, configText);
  const checkKeys = (node: ts.Node): void => {
    if (ts.isObjectLiteralExpression(node)) {
      const keys = new Set<string>();
      for (const property of node.properties) {
        const key = (property.name as ts.StringLiteral).text.toLowerCase();
        if (keys.has(key)) throw new Error("Package config has duplicate or ASCII-case-colliding JSON members.");
        keys.add(key);
      }
    }
    ts.forEachChild(node, checkKeys);
  };
  checkKeys(configTree);
  const config = validateConfig(value);
  const outputDirectory = config.outputDirectory ?? "dist";
  if (portablePathsContainOneAnother(outputDirectory, RPC_BINDINGS_OUTPUT_PATH)) {
    throw new Error(`outputDirectory '${outputDirectory}' must not equal, contain, or be contained by the owned RPC binding output '${RPC_BINDINGS_OUTPUT_PATH}'.`);
  }
  for (const assets of Object.values(config.nativeAssets ?? {})) {
    for (const asset of assets) {
      if (portablePathsContainOneAnother(outputDirectory, asset.sourcePath)) {
        throw new Error(`Native asset source '${asset.sourcePath}' must not be inside or contain outputDirectory '${outputDirectory}'.`);
      }
    }
  }
  const outputRoot = resolveInside(root, outputDirectory, "outputDirectory");
  if (config.services !== undefined && portablePathsContainOneAnother(outputDirectory, config.services.module)) throw new Error("Service source must remain outside generated outputDirectory.");
  const project = { root, configPath, config, outputRoot, contracts: [] };
  return { project, configBytes };
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

function validateConfig(value: unknown): ResolvedNodePackageConfig {
  const config = record(value, "sunder.package.json");
  if (config.schemaVersion !== 3) throw new Error("sunder.package.json schemaVersion must be 3. Migrate descriptor/router authoring to defineService and services.module; legacy authoring modes are retired.");
  const allowed = new Set(["schemaVersion", "id", "name", "summary", "version", "entry", "workerCapabilities", "contracts", "providers", "usesContracts", "dependencies", "outputDirectory", "maximumPackageBytes", "app", "rpcBindings", "nativeAssets"]);
  if (config.schemaVersion === 3) {
    for (const key of ["contracts", "providers", "usesContracts", "rpcBindings"]) allowed.delete(key);
    allowed.add("services");
  }
  for (const key of Object.keys(config)) if (!allowed.has(key)) throw new Error(`sunder.package.json contains unknown property '${key}'.`);
  let services: ResolvedNodePackageConfig["services"];
  if (config.schemaVersion === 3 && config.services !== undefined) {
    const input = record(config.services, "services"); only(input, "module");
    const module = projectPath(input.module, "services.module", 240, 32);
    if (!/\.(ts|js)$/u.test(module) || module.toLowerCase().endsWith(".d.ts") || module.toLowerCase().startsWith(".sunder/") || module.toLowerCase().startsWith("node_modules/")) throw new Error("services.module must be authored .ts or .js source outside generated output.");
    if (config.entry !== undefined) throw new Error("Service Worker bootstrap is generated; remove entry and export configure from the service module for lifecycle hooks.");
    services = { module };
  }
  const id = text(config.id, "id", 128);
  if (!isPackageId(id)) throw new Error("Package id must be lowercase dot-separated ASCII.");
  const name = displayText(config.name, "name", 256);
  if (services && (typeof config.version !== "string" || !isSemanticVersion(config.version))) throw new Error(`rpc-authoring.private-package-version-invalid: ${String(config.id)}: owning package version '${String(config.version)}' must be strict full SemVer of at most 256 characters.`);
  const version = text(config.version, "version", 256);
  if (!isSemanticVersion(version)) throw new Error("Package version must be strict SemVer 2.0.");
  const workerCapabilities = boundedArray(
    config.workerCapabilities,
    "workerCapabilities",
    SUPPORTED_WORKER_CAPABILITIES.length,
  ).map((item, index) => {
    const capability = text(item, `workerCapabilities[${index}]`, 128);
    if (!SUPPORTED_WORKER_CAPABILITIES.includes(capability as WorkerCapability)) {
      throw new Error(`workerCapabilities[${index}] '${capability}' is not implemented by the TypeScript Worker V1 SDK.`);
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
  const contracts = boundedArray(config.schemaVersion === 3 ? [] : config.contracts, "contracts", MAXIMUM_CONTRACTS).map((item, index) => {
    const contract = record(item, `contracts[${index}]`);
    only(contract, "path", "descriptorPath", "exposure");
    if (typeof contract.path === "string" && contract.path.toLowerCase().startsWith(".sunder/")) throw new Error("Generated descriptor snapshots are not descriptor-first authoring inputs.");
    return {
      path: projectPath(contract.path, `contracts[${index}].path`, 240, 32),
      descriptorPath: contract.descriptorPath === undefined
        ? undefined
        : logicalPath(contract.descriptorPath, `contracts[${index}].descriptorPath`),
      exposure: oneOf(contract.exposure, `contracts[${index}].exposure`, "package", "public"),
    };
  });
  const providers = boundedArray(config.schemaVersion === 3 ? [] : config.providers, "providers", MAXIMUM_PROVIDERS).map((item, index) => {
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
    validateDependencyVersionRange(versionRange, `dependencies[${index}].versionRange`);
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
  const nativeAssets = config.nativeAssets === undefined
    ? undefined
    : validateNativeAssets(config.nativeAssets, contracts);
  requireUnique(contracts.map((contract) => contract.path), "Contract path");
  requireUnique(providers.map((provider) => `${provider.role}/${provider.providerId}`), "Provider role/id");
  requireUnique(usesContracts.map((use) => `${use.role}/${use.contractId}`), "Contract use role/id");
  requireUnique(dependencies.map((dependency) => dependency.packageId), "Dependency package id");
  requireUnique((rpcBindings ?? []).map((binding) => binding.path), "RPC binding input path");
  validateRpcBindingOutputPaths(rpcBindings ?? []);
  const result: ResolvedNodePackageConfig = {
    schemaVersion: config.schemaVersion,
    ...(services === undefined ? {} : { services }),
    id,
    name,
    version,
    entry: services ? ".sunder/generated/rpc/worker.ts" : projectPath(config.entry, "entry", 240, 32),
    workerCapabilities,
    contracts,
    providers,
    usesContracts,
    dependencies,
    ...(rpcBindings === undefined ? {} : { rpcBindings }),
    ...(nativeAssets === undefined ? {} : { nativeAssets }),
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
    ...(config.maximumPackageBytes === undefined ? {} : { maximumPackageBytes: boundedPositiveInteger(config.maximumPackageBytes, "maximumPackageBytes", MAXIMUM_COMPRESSED_PACKAGE_BYTES) }),
    ...(app === undefined ? {} : { app }),
  };
}

function validateNativeAssets(
  value: unknown,
  contracts: readonly ContractConfig[],
): NativeAssetsConfig {
  const configured = record(value, "nativeAssets");
  only(configured, ...SUPPORTED_RIDS);
  if (Object.keys(configured).length === 0) throw new Error("nativeAssets must declare at least one exact RID.");
  const result: Partial<Record<SunderRid, readonly NativeAssetConfig[]>> = {};
  let total = 0;
  for (const rid of SUPPORTED_RIDS) {
    if (!Object.hasOwn(configured, rid)) continue;
    const values = boundedArray(configured[rid], `nativeAssets.${rid}`, MAXIMUM_NATIVE_ASSETS);
    if (values.length === 0) throw new Error(`nativeAssets.${rid} must contain at least one asset.`);
    if (values.length > MAXIMUM_NATIVE_ASSETS - total) {
      throw new Error(`nativeAssets may contain at most ${MAXIMUM_NATIVE_ASSETS} entries across all exact RIDs.`);
    }
    total += values.length;
    const assets = values.map((item, index): NativeAssetConfig => {
      const asset = record(item, `nativeAssets.${rid}[${index}]`);
      only(asset, "sourcePath", "targetPath");
      const sourcePath = projectPath(asset.sourcePath, `nativeAssets.${rid}[${index}].sourcePath`, 240, 32);
      const targetPath = logicalPath(asset.targetPath, `nativeAssets.${rid}[${index}].targetPath`);
      assertNotExecutionRuntimeAssetPath(sourcePath, `nativeAssets.${rid}[${index}].sourcePath`);
      assertNotExecutionRuntimeAssetPath(targetPath, `nativeAssets.${rid}[${index}].targetPath`);
      return { sourcePath, targetPath };
    });
    validateNativeAssetTargetPaths(rid, assets, contracts);
    result[rid] = assets;
  }
  return result;
}

function validateNativeAssetTargetPaths(
  rid: SunderRid,
  assets: readonly NativeAssetConfig[],
  contracts: readonly ContractConfig[],
): void {
  const paths = [
    { path: "contracts/rpc-model-projection.v1.json", label: "generated model projection" },
    { path: "bin/worker.cjs", label: "generated Worker entry point" },
    { path: "THIRD-PARTY-NOTICES/Bundled-Worker-Dependencies.txt", label: "generated Worker notice" },
    ...contracts.map((contract) => ({
      path: contract.descriptorPath ?? `contracts/${basename(contract.path)}`,
      label: `contract descriptor '${contract.path}'`,
    })),
    ...assets.map((asset, index) => ({
      path: asset.targetPath,
      label: `nativeAssets.${rid}[${index}].targetPath`,
    })),
  ];
  const entries = new Map<string, { readonly path: string; readonly kind: "directory" | "file"; readonly label: string }>();
  for (const item of paths) {
    const segments = item.path.split("/");
    for (let index = 1; index <= segments.length; index++) {
      const candidate = segments.slice(0, index).join("/");
      const kind = index === segments.length ? "file" : "directory";
      const folded = asciiCaseFold(candidate);
      const existing = entries.get(folded);
      if (existing !== undefined && (kind === "file" || existing.path !== candidate || existing.kind !== kind)) {
        throw new Error(`${item.label} '${item.path}' collides with ${existing.label} '${existing.path}' on portable filesystems.`);
      }
      entries.set(folded, { path: candidate, kind, label: item.label });
    }
  }
}

function assertNotExecutionRuntimeAssetPath(path: string, label: string): void {
  const segments = path.toLowerCase().split("/");
  const fileName = segments.at(-1)!;
  const forbiddenNodeFile = fileName === "node"
    || fileName === "node.exe"
    || fileName === "node.dll"
    || fileName === "node.lib"
    || fileName === "libnode.dylib"
    || fileName === "node.js-license.txt"
    || fileName === "node.js-license"
    || fileName === "node_pdb"
    || fileName === "node_etw_provider.man"
    || fileName === "node.h"
    || fileName.startsWith("libnode.so");
  const forbiddenDotNetFile = [
    "apphost", "apphost.exe", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
    "libcoreclr.dylib", "libcoreclr.so", "libhostfxr.dylib", "libhostfxr.so",
    "libhostpolicy.dylib", "libhostpolicy.so", "libclrjit.dylib", "libclrjit.so",
    "clrjit.dll", "mscordaccore.dll", "mscordbi.dll", "system.private.corelib.dll",
    "dotnet", "dotnet.exe",
  ].includes(fileName);
  const includesToolchain = segments.some((segment) => /^node-v\d/iu.test(segment))
    || segments.some((segment, index) => segment === "node_modules"
      && (segments[index + 1] === "npm" || segments[index + 1] === "corepack"))
    || segments.some((segment, index) => segment === "shared"
      && segments[index + 1] === "microsoft.netcore.app")
    || segments.some((segment, index) => segment === "packs"
      && segments[index + 1]?.startsWith("microsoft.netcore.app.runtime.") === true);
  if (forbiddenNodeFile || forbiddenDotNetFile || includesToolchain) {
    throw new Error(`${label} '${path}' must not include a Node or .NET execution runtime or toolchain asset.`);
  }
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
