#!/usr/bin/env node
import { resolve } from "node:path";
import { aggregateAndPack, assertRpcReleaseSupported, buildDevPackage, buildWebTarget, prepareProject } from "./package";
import { buildModuleTarget, smokeModuleTarget } from "./module";
import { SUPPORTED_RIDS, type SunderRid } from "./model";
import { generateRpcBindings } from "./rpc-bindings";
import { contractsCommand } from "./rpc-contracts";
import type { AcceptOptions } from "./rpc-baseline";
import { restoreContracts, type ContractRestoreOptions } from "./rpc-contract-restore";
import { collectContractCache } from "./rpc-contract-cache-gc";
import { DEFAULT_CONTRACT_CACHE } from "./rpc-contract-cache";
import { packageApiCommand, type PackageApiRequest } from "./rpc-package-commands";
import { importContractPublication } from "./rpc-publication";
import { consumerRoles } from "./rpc-contract-lock";

async function main(): Promise<void> {
  const [command, ...rawArguments] = process.argv.slice(2);
  const modeFlags = rawArguments.filter(value => value === "--development-registry");
  if (modeFlags.length > 1) throw new Error("Duplicate Development Registry option.");
  const registryOptions = { allowDevelopmentRegistryOrigin: modeFlags.length === 1 };
  const argumentsList = rawArguments.filter(value => value !== "--development-registry");
  if (command === undefined || command === "--help" || command === "-h") {
    help();
    return;
  }
  if (command === "api" || command === "platform") {
    const { project, request } = command === "platform" ? parsePlatformApi(argumentsList) : parsePackageApi(argumentsList);
    const result = await packageApiCommand(project, { ...request, ...registryOptions });
    process.stdout.write(result.changed ? "Committed package API lock changes. Run generate/build to refresh outputs.\n" : "Package API lock is unchanged.\n");
    for (const change of result.roleChanges ?? []) process.stdout.write(`${change.packageId}: consumer roles ${change.before?.join(",") ?? "missing (legacy runtime)"} -> ${change.after.join(",")}\n`);
    for (const artifact of result.lock.packageArtifacts.values()) {
      const consumer = result.lock.packageImports.get(artifact.packageId);
      process.stdout.write(`${artifact.packageId} ${artifact.selectedPackageVersion}; range=${artifact.requestedVersionRange}; prerelease=${artifact.includePrerelease}; presence=${consumer?.presence ?? "provider-only"}; contracts=${artifact.publicContractCount}\n`);
      for (const contract of artifact.contracts.values()) process.stdout.write(`  ${contract.contractId} ${contract.contractVersion}; descriptor=${contract.descriptorSha256}; ABI=${contract.abiSha256}; required=${consumer?.requiredContracts.includes(contract.contractId) ?? false}\n`);
    }
    for (const [key, imported] of result.lock.platformImports)
      process.stdout.write(`platform ${key}: ${imported.contractId} ${imported.resolvedContractVersion}; catalog=${imported.selectedAuthorityContentIdentity}; registry=${imported.registryOrigin ?? "local"}\n`);
    if (request.candidate !== undefined) process.stdout.write("package-api.candidate-authority-unconfirmed: Candidate bytes support local builds; Registry must confirm ownership before dependent publication.\n");
    return;
  }
  if (command === "contracts") {
    if (argumentsList[0] === "publication") {
      if (argumentsList[1] !== "import") throw new Error("contracts publication requires import.");
      const values = new Map<string, string>();
      for (let index = 2; index < argumentsList.length; index++) {
        const option = argumentsList[index]!;
        if (!["--project", "--package", "--registry"].includes(option) || values.has(option)) throw new Error("Publication import accepts unique project/package/registry inputs only.");
        const value = requiredValue(argumentsList, ++index, option); if (!value.trim()) throw new Error("Publication import inputs must be nonempty.");
        values.set(option, value);
      }
      if (!values.has("--package") || !values.has("--registry")) throw new Error("Publication import requires an explicit retained --package and --registry origin.");
      const changed = await importContractPublication(resolve(values.get("--project") ?? process.cwd()),
        { ...registryOptions, packagePath: resolve(values.get("--package")!), registryOrigin: values.get("--registry")! });
      process.stdout.write(changed ? "Imported exact Registry publication evidence into the owned baseline.\n" : "Exact publication evidence is already retained; no files changed.\n");
      return;
    }
    if (argumentsList[0] === "gc") {
      if (registryOptions.allowDevelopmentRegistryOrigin) throw new Error("GC does not accept Registry options; reviewed locks already protect Development references.");
      const { cacheRoot, locks, graceDays } = parseCacheGc(argumentsList.slice(1));
      const result = await collectContractCache(cacheRoot, locks, { graceDays });
      process.stdout.write(`Collected ${result.removedEntries} contract entries and ${result.removedArtifacts} retained artifacts; kept ${result.retainedUnprovenArtifacts} unproven artifacts. Pruned entries require explicit restore. More candidates: ${result.moreCandidates}.\n`);
      return;
    }
    if (argumentsList[0] === "restore") {
      const { projectPath, options } = parseRestore(argumentsList.slice(1));
      const count = await restoreContracts(projectPath, { ...options, ...registryOptions });
      process.stdout.write(`Restored ${count} exact RPC descriptor cache entries; lock selections are unchanged.\n`);
      return;
    }
    const { operation, projectPath, options } = parseContracts(argumentsList);
    const changed = await contractsCommand(operation, projectPath, options, registryOptions);
    process.stdout.write(changed ? `Owned contracts ${operation} updated the baseline. Run generate/build to refresh outputs.\n` : "Owned contracts match; no files changed.\n");
    return;
  }
  const options = parse(argumentsList);
  const projectPath = resolve(options.project ?? process.cwd());
  switch (command) {
    case "generate":
      await generateRpcBindings(projectPath, registryOptions);
      break;
    case "dev":
      await buildDevPackage({ ...registryOptions, projectPath, watch: options.watch });
      break;
    case "build":
      await buildModuleTarget({
        ...registryOptions,
        projectPath,
        ...(options.rid === undefined ? {} : { rid: options.rid }),
      });
      await buildWebTarget(projectPath, options.rid, registryOptions);
      break;
    case "smoke":
      await smokeModuleTarget(projectPath, registryOptions);
      break;
    case "package": {
      assertRpcReleaseSupported(await prepareProject(projectPath, registryOptions));
      const discoveryRoot = options.discoveryRoot;
      if (options.leaves.length > 0 && discoveryRoot === undefined) {
        throw new Error("package --leaf requires an explicit --discovery-root shared with the leaf producer.");
      }
      if (discoveryRoot === undefined) {
        for (const rid of SUPPORTED_RIDS) {
          await buildModuleTarget({ ...registryOptions, projectPath, rid });
          await buildWebTarget(projectPath, rid, registryOptions);
        }
      }
      await aggregateAndPack(projectPath, discoveryRoot === undefined
        ? []
        : (options.leaves.length === 0 ? [discoveryRoot] : options.leaves)
          .map((path) => ({ path, discoveryRoot })), registryOptions);
      break;
    }
    default:
      throw new Error(`Unknown command '${command}'. Run sunder-package --help.`);
  }
}

function parsePlatformApi(args: readonly string[]): { project: string; request: PackageApiRequest } {
  const operation = args[0];
  if (operation !== "implement" && operation !== "update" && operation !== "unimplement" && operation !== "list") throw new Error("platform requires implement, update, unimplement, or list.");
  const positional: string[] = []; const values = new Map<string, string>();
  for (let index = 1; index < args.length; index++) {
    const argument = args[index]!;
    if (!argument.startsWith("-")) { positional.push(argument); continue; }
    if (!["--project", "--cache-root", "--registry", "--local-name", "--platform-catalog"].includes(argument) || values.has(argument))
      throw new Error("Platform commands accept unique project/cache-root/registry/local-name/platform-catalog options; catalog versions follow the coordinated SDK line.");
    const value = requiredValue(args, ++index, argument); if (!value.trim()) throw new Error("Platform arguments must be nonempty."); values.set(argument, value);
  }
  if (operation === "update" ? positional.length > 1 : positional.length !== (operation === "list" ? 0 : 1)) throw new Error("Platform command has missing or extra contract arguments.");
  return { project: resolve(values.get("--project") ?? process.cwd()), request: { operation: `platform-${operation}`,
    ...(positional[0] === undefined ? {} : { contractId: positional[0] }),
    ...(values.has("--registry") ? { registryOrigin: values.get("--registry")! } : {}), ...(values.has("--local-name") ? { localName: values.get("--local-name")! } : {}),
    ...(values.has("--platform-catalog") ? { platformCatalog: resolve(values.get("--platform-catalog")!) } : {}),
    ...(values.has("--cache-root") ? { cacheRoot: resolve(values.get("--cache-root")!) } : {}),
  } };
}

function parsePackageApi(args: readonly string[]): { project: string; request: PackageApiRequest } {
  const operation = args[0];
  if (operation !== "add" && operation !== "update" && operation !== "remove" && operation !== "list" && operation !== "require" && operation !== "unrequire" && operation !== "implement" && operation !== "unimplement" && operation !== "migrate-consumer-roles") throw new Error("api requires add, update, remove, list, require, unrequire, implement, unimplement, or migrate-consumer-roles.");
  const positional: string[] = []; const values = new Map<string, string>(); const seen = new Set<string>(); let prerelease: boolean | undefined;
  for (let index = 1; index < args.length; index++) {
    const argument = args[index]!;
    if (!argument.startsWith("-")) { positional.push(argument); continue; }
    if (seen.has(argument)) throw new Error("Duplicate package API option."); seen.add(argument);
    if (argument === "--include-prerelease" || argument === "--exclude-prerelease") {
      if (prerelease !== undefined) throw new Error("Prerelease include/exclude flags are mutually exclusive."); prerelease = argument === "--include-prerelease"; continue;
    }
    if (!["--project", "--cache-root", "--version", "--registry", "--presence", "--local-name", "--candidate", "--platform-catalog", "--consumer-roles"].includes(argument)) throw new Error("Unknown package API option.");
    const value = requiredValue(args, ++index, argument); if (!value.trim()) throw new Error("Package API values must be nonempty."); values.set(argument, value);
  }
  const count = operation === "list" ? 0 : ["require", "unrequire", "implement", "unimplement"].includes(operation) ? 2 : 1;
  if (operation === "update" || operation === "migrate-consumer-roles" ? positional.length > 1 : positional.length !== count) throw new Error("Package API command has missing or extra target arguments.");
  const presence = values.get("--presence"); if (presence !== undefined && presence !== "required" && presence !== "optional") throw new Error("Package presence must be exactly required or optional.");
  return { project: resolve(values.get("--project") ?? process.cwd()), request: { operation,
    ...(positional[0] === undefined ? {} : { packageId: positional[0] }), ...(positional[1] === undefined ? {} : { contractId: positional[1] }),
    ...(values.has("--version") ? { versionRange: values.get("--version")! } : {}), ...(values.has("--registry") ? { registryOrigin: values.get("--registry")! } : {}),
    ...(presence === undefined ? {} : { presence }), ...(prerelease === undefined ? {} : { includePrerelease: prerelease }),
    ...(values.has("--consumer-roles") ? { consumerRoles: consumerRoles(values.get("--consumer-roles")!.split(",")) } : {}),
    ...(values.has("--local-name") ? { localName: values.get("--local-name")! } : {}),
    ...(values.has("--candidate") ? { candidate: resolve(values.get("--candidate")!) } : {}),
    ...(values.has("--platform-catalog") ? { platformCatalog: resolve(values.get("--platform-catalog")!) } : {}),
    ...(values.has("--cache-root") ? { cacheRoot: resolve(values.get("--cache-root")!) } : {}),
  } };
}

function parseCacheGc(args: readonly string[]): { cacheRoot: string; locks: string[]; graceDays: number } {
  let cacheRoot = DEFAULT_CONTRACT_CACHE; let graceDays = 7; const locks: string[] = []; const seen = new Set<string>();
  for (let index = 0; index < args.length; index++) {
    const option = args[index]!;
    if (!["--lock", "--cache-root", "--grace-days"].includes(option) || option !== "--lock" && seen.has(option))
      throw new Error("contracts gc requires explicit reviewed --lock paths, optional --cache-root and --grace-days; selection/Registry inputs are forbidden.");
    seen.add(option); const value = requiredValue(args, ++index, option);
    if (!value.trim()) throw new Error("contracts gc inputs must be nonempty.");
    if (option === "--lock") locks.push(resolve(value));
    else if (option === "--cache-root") cacheRoot = resolve(value);
    else {
      if (!/^[1-9][0-9]{0,2}$/u.test(value) || Number(value) > 365) throw new Error("GC grace must be an integer from 1 through 365 days.");
      graceDays = Number(value);
    }
  }
  if (!locks.length || locks.length > 128) throw new Error("contracts gc requires 1..128 explicit reviewed --lock paths.");
  return { cacheRoot, locks, graceDays };
}

function parseRestore(args: readonly string[]): { projectPath: string; options: ContractRestoreOptions } {
  const values = new Map<string, string>();
  for (let index = 0; index < args.length; index++) {
    const option = args[index]!;
    if (!["--project", "--cache-root", "--candidate", "--platform-catalog"].includes(option) || values.has(option)) throw new Error("contracts restore accepts only unique project/cache-root/candidate/platform-catalog inputs; selection and origin overrides are forbidden.");
    const value = requiredValue(args, ++index, option);
    if (!value.trim()) throw new Error("contracts restore inputs must be nonempty; offline inputs cannot fall back to network.");
    values.set(option, value);
  }
  return { projectPath: resolve(values.get("--project") ?? process.cwd()), options: {
    ...(values.has("--cache-root") ? { cacheRoot: resolve(values.get("--cache-root")!) } : {}),
    ...(values.has("--candidate") ? { candidate: resolve(values.get("--candidate")!) } : {}),
    ...(values.has("--platform-catalog") ? { platformCatalog: resolve(values.get("--platform-catalog")!) } : {}),
  } };
}

function parseContracts(args: readonly string[]): { operation: "check" | "accept" | "freeze" | "migrate-private"; projectPath: string; options: AcceptOptions } {
  const operation = args[0];
  if (operation !== "check" && operation !== "accept" && operation !== "freeze" && operation !== "migrate-private") throw new Error("contracts requires check, accept, freeze, or migrate-private.");
  let key: string | undefined; let project = process.cwd(); let currentVersion: string | undefined; let nextVersion: string | undefined; let acknowledgeBreakingChange = false;
  const seen = new Set<string>();
  for (let index = 1; index < args.length; index++) {
    const arg = args[index]!;
    if (!arg.startsWith("-")) { if (key !== undefined || operation === "check") throw new Error("contracts check takes no key; accept/freeze take at most one exact key."); key = arg; continue; }
    if (seen.has(arg)) throw new Error(`Duplicate contracts option '${arg}'.`); seen.add(arg);
    if (arg === "--project") project = requiredValue(args, ++index, arg);
    else if (operation === "accept" && arg === "--current-version") currentVersion = requiredValue(args, ++index, arg);
    else if (operation === "accept" && arg === "--next-version") nextVersion = requiredValue(args, ++index, arg);
    else if (operation === "accept" && arg === "--acknowledge-breaking-change") acknowledgeBreakingChange = true;
    else throw new Error(`Unknown contracts ${operation} option '${arg}'.`);
  }
  // Whether next-only selection is valid depends on the selected key's actual
  // baseline/history. Leave that decision to the source-backed planner.
  if ((currentVersion !== undefined || nextVersion !== undefined || acknowledgeBreakingChange) && key === undefined
    || currentVersion !== undefined && nextVersion === undefined
    || acknowledgeBreakingChange && (currentVersion === undefined || nextVersion === undefined)) throw new Error("Version selection requires an exact key; existing versions and breaking acknowledgement require a current/next version pair.");
  return { operation, projectPath: resolve(project), options: { ...(key === undefined ? {} : { key }), ...(currentVersion === undefined ? {} : { currentVersion }), ...(nextVersion === undefined ? {} : { nextVersion }), acknowledgeBreakingChange } };
}

function parse(argumentsList: readonly string[]): CliOptions {
  let project: string | undefined;
  let rid: SunderRid | undefined;
  let watch = false;
  let discoveryRoot: string | undefined;
  const leaves: string[] = [];
  for (let index = 0; index < argumentsList.length; index++) {
    const argument = argumentsList[index];
    switch (argument) {
      case "--project":
        project = requiredValue(argumentsList, ++index, argument);
        break;
      case "--rid": {
        const value = requiredValue(argumentsList, ++index, argument);
        if (!SUPPORTED_RIDS.includes(value as SunderRid)) throw new Error(`Unsupported exact RID '${value}'.`);
        rid = value as SunderRid;
        break;
      }
      case "--leaf":
        leaves.push(resolve(requiredValue(argumentsList, ++index, argument)));
        break;
      case "--discovery-root":
        discoveryRoot = resolve(requiredValue(argumentsList, ++index, argument));
        break;
      case "--watch":
        watch = true;
        break;
      default:
        throw new Error(`Unknown option '${argument}'.`);
    }
  }
  return { project, rid, watch, discoveryRoot, leaves };
}

function requiredValue(argumentsList: readonly string[], index: number, option: string): string {
  const value = argumentsList[index];
  if (value === undefined || value.startsWith("--")) throw new Error(`${option} requires a value.`);
  return value;
}

function help(): void {
  process.stdout.write("Services: schemaVersion 3 uses services: { module: 'src/services.ts' } (or checked .js). Export defineService definitions; Worker bootstrap and #sunder/api are generated. Private versions follow the full package SemVer and need no Accept/Freeze. Public services retain explicit review.\n\nMigrations: contracts migrate-private [key] --project <root> archives complete eligible legacy private records. api migrate-consumer-roles [package] --project <root> preserves omitted legacy Runtime-only intent; targeted --consumer-roles app|runtime|app,runtime explicitly reviews widening. Add defaults to both; targeted Update preserves intent unless that option is supplied.\n\n");
  process.stdout.write("Development Registry: --development-registry explicitly permits canonical http://localhost[:nondefault-port]/ origins for this invocation, including offline generation/build/pack. Other HTTP hosts and locked-origin overrides remain rejected. HTTPS is the default.\n\n");
  process.stdout.write("Platform providers: platform implement <contract> [--platform-catalog <catalog.json>] [--registry <origin>] [--local-name <key>] | platform update [contract] | platform unimplement <contract> | platform list\nSelect an explicit local catalog for Registry-independent development, or a Registry for published catalogs. Versions follow the coordinated SDK line. Local updates require --platform-catalog; targeted --registry updates can bind a local selection, but cannot override an existing Registry origin.\n\n");
  process.stdout.write("Package APIs: api add <package> --version <range> [--candidate <archive>] [--registry <origin>] [--presence required|optional] | api update [package] | api remove <package> | api list | api require|unrequire|implement|unimplement <package> <contract>\nLocal candidate selection requires no Registry. Use --platform-catalog with candidates that import Core APIs. Use --project, --cache-root, and explicit --include-prerelease/--exclude-prerelease as applicable. Add/implement support --local-name for the generated facade/provider key. Ordinary generation/build is offline; SUNDER_CONTRACT_CACHE selects a nondefault build cache.\n\n");
  process.stdout.write("Explicit cache GC: contracts gc --lock <reviewed-lock> [--lock <reviewed-lock> ...] [--cache-root <path>] [--grace-days <1..365; default 7>]\nPruned entries require explicit restore. Include all workspace locks you want to retain.\n\n");
  process.stdout.write("Exact contract cache: contracts restore --project <path> [--cache-root <path>] [--candidate <archive.sunderpkg>] [--platform-catalog <catalog.json>]. Local package and catalog inputs can be supplied together. Candidates use the local Sunder CLI via SUNDER_CLI; all content must match the exact lock. Local selections never fall back to a Registry. Lock selections are unchanged.\n\n");
  process.stdout.write("Owned contracts: contracts check --project <path> | contracts accept [key] --project <path> [--current-version <version>] [--next-version <version>] [--acknowledge-breaking-change] | contracts freeze [key] --project <path>\nFresh projects first configure every import, even when complete source names not-yet-selected providers. Exact import and input checks remain active; first Accept compiles the complete graph before generation. Accepted projects retain full source checks; prior receipt/export/marker evidence requires the original baseline. A new selected key may specify a stable --next-version alone; existing versions require a current/next version pair.\n\n");
  process.stdout.write("Publication evidence: contracts publication import --project <path> --package <retained.sunderpkg> --registry <origin>\nVerifies exact Registry publication before marking matching frozen baseline snapshots published. It never uploads a package.\n\n");
  process.stdout.write(`Sunder TypeScript package tooling\n\nCommands:\n  generate Generate configured local RPC TypeScript bindings\n  dev      Build a canonical runtime-backed current-RID package (--watch optional)\n  build    Build a bundled Node 24 Worker module and optional Vite App exact-RID leaves\n  smoke    Build and smoke the development package through the managed Sunder Runtime\n  package  Build all six exact-RID leaves and write a deterministic .sunderpkg\n\nOptions:\n  --project <path>\n  --rid <exact-rid>\n  --leaf <canonical-leaf> (repeatable for package)\n  --discovery-root <path> (required with --leaf)\n  --watch\n`);
}

interface CliOptions {
  readonly project?: string;
  readonly rid?: SunderRid;
  readonly watch: boolean;
  readonly discoveryRoot?: string;
  readonly leaves: readonly string[];
}

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
