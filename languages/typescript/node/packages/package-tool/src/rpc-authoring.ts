import { createHash } from "node:crypto";
import { realpath } from "node:fs/promises";
import { basename, dirname, relative, resolve as resolveNative } from "node:path";
import ts from "typescript";
import {
  authoringBindingModel, canonicalRpcDescriptorBytes, computeRpcAbiSha256,
  generateTypeScriptBindings, isPackageId, parseRpcContractDescriptor,
} from "@sunder/sdk";
import type { ContractUseConfig, LoadedContract, LoadedProject, ProviderConfig } from "./model";
import { readProjectDescriptorFile } from "./descriptor-files";
import { packageToolCompatibility } from "./compatibility";
import { captureRpcProducer } from "./rpc-producer";
import { captureOwnedRuntimeInputs, type OwnedRuntimeInputs } from "./rpc-runtime-inputs";
import { captureRpcCompilerInputs } from "./rpc-compiler-inputs";
import { captureBaseline, exists, verifyFreshOwnedSource, type BaselineInput } from "./rpc-baseline-store";
import { validateBaselineSource, type OwnedBaseline } from "./rpc-baseline";
import { ownedAppClientSource, ownedAppUses } from "./rpc-owned-app";
import { applicableUseActions, ownedRuntimeUses } from "./rpc-owned-uses";
import { parseContractLock, type ContractLock, type RegistryOriginOptions } from "./rpc-contract-lock";
import { DEFAULT_CONTRACT_CACHE } from "./rpc-contract-cache";
import { authority, contractSymbol, importedFacadeReceipt, importProjection, importReceiptArtifacts, preparePackageImports, type ImportedProviderBinding, type PackageImportAnalysis } from "./rpc-package-imports";

const producer = captureRpcProducer([__filename, require.resolve("@sunder/sdk"), require.resolve("typescript"), require.resolve("./compatibility"),
  ...["./model", "./package", "./module", "./rpc-bindings", "./rpc-authoring-plugin", "./rpc-owned-app", "./output", "./notices", "./rpc-runtime-inputs", "./rpc-baseline", "./rpc-baseline-json", "./rpc-baseline-store", "./rpc-evolution", "./rpc-contracts", "./rpc-contract-lock", "./rpc-contract-cache", "./rpc-contract-cache-gc", "./rpc-contract-candidate", "./rpc-contract-restore", "./rpc-package-imports", "./rpc-package-selection", "./rpc-package-commands", "./cli"].map((path) => require.resolve(path)),
  require.resolve("@sunder/package-tool/package.json"), resolve(dirname(require.resolve("@sunder/sdk")), "../package.json"), require.resolve("typescript/package.json"),
  require.resolve("./rpc-platform-catalogs"), require.resolve("./platform-catalog"), require.resolve("./rpc-platform-cache")]);

const MAX_SOURCE_BYTES = 4_194_304;
type Value = null | string | number | boolean | Value[] | { [key: string]: Value } | Call | Handler | Builtin;
class Call { constructor(readonly name: string, readonly args: Value[], readonly importKey?: string) {} }
class Handler { constructor(readonly node: ts.MethodDeclaration | ts.ArrowFunction | ts.FunctionExpression) {} }
class Builtin { constructor(readonly name: string) {} }
type Obj = { [key: string]: Value };
type Schema = Record<string, unknown>;

export interface OwnedRpcAnalysis {
  readonly imports: PackageImportAnalysis;
  readonly producerFiles: readonly string[];
  readonly baselineInput: BaselineInput;
  readonly baseline?: OwnedBaseline;
  readonly physicalFingerprint: string;
  readonly modulePath: string;
  readonly exportName: string;
  readonly source: string;
  readonly files: ReadonlyMap<string, string>;
  readonly contracts: readonly LoadedContract[];
  readonly providers: readonly ProviderConfig[];
  readonly importedProviders: readonly ImportedProviderBinding[];
  readonly appUses: readonly ContractUseConfig[];
  readonly runtimeUses: readonly ContractUseConfig[];
  readonly inputFingerprint: string;
  readonly runtime: OwnedRuntimeInputs;
  readonly revalidate: () => Promise<void>;
}

export interface OwnedAnalysisOptions extends RegistryOriginOptions {
  readonly contractLock?: ContractLock;
  readonly contractCacheRoot?: string;
  /** Selection commands alone may stage one not-yet-implemented provider surface. */
  readonly allowUnboundImportedProviderKeys?: ReadonlySet<string>;
  readonly physicalBaseline?: BaselineInput;
  readonly identityBaseline?: OwnedBaseline;
  readonly allowDrift?: boolean;
  readonly requireBaseline?: boolean;
}

/** Initial dependency configuration proves inputs and generated import surfaces,
 * but deliberately does not claim an accepted/compiled source graph. */
export async function prepareInitialApiSelection(project: LoadedProject, configBytes: Buffer, lock: ContractLock,
  cacheRoot: string, baseline: BaselineInput): Promise<() => Promise<void>> {
  if (baseline.baseline || !project.config.rpc) fail("baseline-drift", "Initial dependency configuration requires fresh V3 RPC source.");
  await verifyFreshOwnedSource(project.root, baseline); producer.revalidate();
  const imports = await preparePackageImports(lock, cacheRoot, project.config.id, project.config.dependencies ?? []);
  const canonicalRoot = await realpath(project.root);
  const inputs = captureRpcCompilerInputs(project.root, canonicalRoot);
  const generatedRoot = resolve(project.root, ".sunder/generated/rpc");
  const canonicalGeneratedRoot = resolve(canonicalRoot, ".sunder/generated/rpc");
  const virtual = new Map([...imports.sources].map(([name, text]) => [resolve(generatedRoot, name), text]));
  virtual.set(resolve(generatedRoot, "imports.ts"), imports.facadeSource);
  const overlay = (path: string): string | undefined => {
    path = resolve(path);
    for (const base of [generatedRoot, canonicalGeneratedRoot]) if (path === base || path.startsWith(base + "/")) return generatedRoot + path.slice(base.length);
    return undefined;
  };
  const host = ts.createCompilerHost(ownedCompilerOptions(resolve(dirname(require.resolve("@sunder/sdk"))), generatedRoot));
  const probes = new Map<string, boolean>(); const directories = new Map<string, boolean>(); const listings = new Map<string, string>();
  const baseExists = host.fileExists.bind(host); const baseDirectory = host.directoryExists!.bind(host); const baseList = host.getDirectories!.bind(host);
  const bounded = (): void => { if (probes.size + directories.size + listings.size > 8192) fail("source-limit-exceeded", "Initial source resolution exceeds its probe budget."); };
  host.readFile = path => { const mapped = overlay(path); return mapped === undefined ? inputs.readFile(path) : virtual.get(mapped); };
  host.fileExists = path => { const mapped = overlay(path); if (mapped !== undefined) return virtual.has(mapped); bounded(); const value = baseExists(path); probes.set(path, value); return value; };
  host.directoryExists = path => {
    if ([generatedRoot, canonicalGeneratedRoot].some(base => base === resolve(path) || base.startsWith(resolve(path) + "/"))) return true;
    const mapped = overlay(path); if (mapped !== undefined) return [...virtual.keys()].some(file => file.startsWith(mapped + "/"));
    bounded(); const value = baseDirectory(path); directories.set(path, value); return value;
  };
  host.getDirectories = path => { if (overlay(path) !== undefined) return []; bounded(); const value = baseList(path); listings.set(path, JSON.stringify(value.sort())); return value; };
  host.realpath = path => overlay(path) ?? inputs.realpath(path);
  const roots = [resolve(project.root, project.config.rpc.module), resolve(project.root, project.config.entry)];
  for (const path of roots) if (inputs.readFile(path) === undefined) fail("unsupported-source", "RPC module and Worker entry must exist as regular project files.");
  // Resolution captures the source graph, including failed probes for not-yet-
  // selected imports. Semantic/provider validation belongs to first Accept.
  const program = ts.createProgram([...roots, ...virtual.keys()], ownedCompilerOptions(resolve(dirname(require.resolve("@sunder/sdk"))), generatedRoot), host);
  // Generated-name/materialization errors are never deferred with authored
  // provider diagnostics. Check every restored import module even if unused yet.
  for (const path of virtual.keys()) {
    const file = program.getSourceFile(path)!;
    const diagnostics = [...program.getSyntacticDiagnostics(file), ...program.getSemanticDiagnostics(file)];
    if (diagnostics.length) fail("generated-name-collision", ts.formatDiagnostics(diagnostics,
      { getCanonicalFileName: path => path, getCurrentDirectory: () => project.root, getNewLine: () => "\n" }));
  }
  inputs.assertValid();
  const verify = async (): Promise<void> => {
    producer.revalidate(); await verifyFreshOwnedSource(project.root, baseline);
    if (!(await readProjectDescriptorFile(project.root, project.configPath, "Package config")).equals(configBytes)) fail("unsupported-source", "Package configuration changed during dependency selection.");
    inputs.revalidate();
    for (const [path, value] of probes) if (baseExists(path) !== value) fail("unsupported-source", "Initial source resolution changed during dependency selection.");
    for (const [path, value] of directories) if (baseDirectory(path) !== value) fail("unsupported-source", "Initial source directory changed during dependency selection.");
    for (const [path, value] of listings) if (JSON.stringify(baseList(path).sort()) !== value) fail("unsupported-source", "Initial source directory membership changed during dependency selection.");
  };
  await verify(); return verify;
}

function ownedCompilerOptions(sdkRoot: string, generatedRoot: string): ts.CompilerOptions {
  if (ts.version !== "6.0.3") fail("unsupported-source", "RPC analysis requires the pinned TypeScript 6.0.3 compiler.");
  return {
    target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
    customConditions: ["node"], strict: true, allowJs: true, checkJs: true, noEmit: true, skipLibCheck: true,
    types: ["node"], typeRoots: [dirname(dirname(require.resolve("@types/node/package.json")))],
    paths: { "@sunder/sdk": [resolve(sdkRoot, "index.d.ts")], "@sunder/sdk/core": [resolve(sdkRoot, "core.d.ts")], "@sunder/sdk/browser": [resolve(sdkRoot, "browser.d.ts")], "#sunder/rpc": [resolve(generatedRoot, "imports.ts")] },
  };
}
export async function analyzeOwnedRpc(project: LoadedProject, configBytes: Buffer, analysisOptions: OwnedAnalysisOptions = {}): Promise<OwnedRpcAnalysis> {
  producer.revalidate();
  const rootConfig = project.config.rpc!;
  const canonicalRoot = await realpath(project.root);
  const compilerInputs = captureRpcCompilerInputs(project.root, canonicalRoot);
  const modulePath = resolve(project.root, rootConfig.module);
  const entryPath = resolve(project.root, project.config.entry);
  const source = compilerInputs.readFile(modulePath);
  const entry = compilerInputs.readFile(entryPath);
  if (source === undefined || entry === undefined) fail("unsupported-source", "RPC source and Worker entry must exist as regular project files.");
  if (source.charCodeAt(0) === 0xfeff) fail("unsupported-source", "RPC source must not contain a BOM.");
  if (/@ts-(nocheck|ignore|expect-error)\b/u.test(source)) fail("unsupported-source", "RPC source must typecheck without suppression directives.");
  const lockPath = resolve(project.root, "sunder-contracts.lock.json");
  const physicalLock = await readProjectDescriptorFile(project.root, lockPath, "RPC source requires a canonical sunder-contracts.lock.json", 33_554_432);
  const baselineInput = analysisOptions.physicalBaseline ?? await captureBaseline(project.root);
  await baselineInput.revalidate();
  const baseline = analysisOptions.identityBaseline ?? baselineInput.baseline;
  const lock = analysisOptions.contractLock?.exactBytes ?? physicalLock;
  const parsedLock = parseContractLock(lock, analysisOptions.allowDevelopmentRegistryOrigin);
  const imports = await preparePackageImports(parsedLock, analysisOptions.contractCacheRoot ?? DEFAULT_CONTRACT_CACHE, project.config.id, project.config.dependencies ?? []);
  if (baseline && baseline.packageId !== project.config.id) fail("baseline-drift", "Owned baseline authority differs from source package identity.");
  const baselineMarker = resolve(project.root, ".sunder/generated/rpc/owned-baseline.sha256");
  const verifyBaselineEvidence = async (): Promise<void> => {
    if (baseline) return;
    if (analysisOptions.requireBaseline) fail("baseline-drift", "Existing owned baseline is required; restore it before checking, freezing or release packing.");
    const markerPresent = await exists(baselineMarker);
    if (markerPresent ? (await readProjectDescriptorFile(project.root, baselineMarker, "Owned baseline receipt marker", 128)).toString() !== "absent\n"
      : (await Promise.all(["authoring-receipt.v1.json", "package-export.v1.json"].map(name => exists(resolve(project.root, ".sunder/generated/rpc", name))))).some(Boolean)) {
      fail("baseline-drift", "The accepted owned baseline was removed; restore it. Development cannot bootstrap existing release evidence.");
    }
  };
  await verifyBaselineEvidence();

  const sdkRoot = resolve(dirname(require.resolve("@sunder/sdk")));
  const generatedRoot = resolve(project.root, ".sunder/generated/rpc");
  const canonicalGeneratedRoot = resolve(canonicalRoot, ".sunder/generated/rpc");
  const canonicalModulePath = resolve(canonicalRoot, rootConfig.module);
  const virtualFiles = new Map<string, string>();
  const overlayPath = (path: string): string | undefined => {
    path = resolve(path);
    for (const base of [generatedRoot, canonicalGeneratedRoot]) if (path === base || path.startsWith(`${base}/`)) return generatedRoot + path.slice(base.length);
    return undefined;
  };
  const virtualDirectory = (path: string): boolean => [generatedRoot, canonicalGeneratedRoot].some((base) => base === resolve(path) || base.startsWith(`${resolve(path)}/`));
  const options = ownedCompilerOptions(sdkRoot, generatedRoot);
  // Capture positive and negative resolution probes as well as bytes. Adding a
  // previously absent module must invalidate a waiting plan, not reuse old types.
  const reads = new Map<string, string | undefined>();
  const probes = new Map<string, boolean>();
  const directoryProbes = new Map<string, boolean>();
  const directories = new Map<string, string>();
  const host = ts.createCompilerHost(options);
  const baseExists = host.fileExists.bind(host);
  const baseDirectories = host.getDirectories!.bind(host);
  const baseDirectoryExists = host.directoryExists!.bind(host);
  host.readFile = (path) => {
    const overlay = overlayPath(path);
    if (overlay !== undefined) return virtualFiles.get(overlay);
    if (reads.has(path)) return reads.get(path);
    const text = compilerInputs.readFile(path); reads.set(path, text); return text;
  };
  host.realpath = (path) => overlayPath(path) ?? compilerInputs.realpath(path);
  host.fileExists = (path) => { const overlay = overlayPath(path); if (overlay !== undefined) return virtualFiles.has(overlay); const exists = baseExists(path); probes.set(path, exists); return exists; };
  host.getDirectories = (path) => { if (overlayPath(path) !== undefined || virtualDirectory(path)) return []; const list = baseDirectories(path); directories.set(path, JSON.stringify(list.sort())); return list; };
  host.directoryExists = (path) => { if (virtualDirectory(path)) return true; const overlay = overlayPath(path); if (overlay !== undefined) return [...virtualFiles.keys()].some((file) => file.startsWith(`${overlay}/`)); const exists = baseDirectoryExists(path); directoryProbes.set(path, exists); return exists; };
  const { toolVersion, sdkVersion, sdkVersionRange, sdkBaselineCapability } = packageToolCompatibility();
  for (const path of [resolve(sdkRoot, "../package.json"), require.resolve("typescript/package.json"), require.resolve("@sunder/package-tool/package.json")]) host.readFile(path);
  if (ts.version !== "6.0.3") fail("unsupported-source", "RPC analysis requires the pinned TypeScript 6.0.3 compiler.");
  for (const [name, source] of imports.sources) virtualFiles.set(resolve(generatedRoot, name), source);
  virtualFiles.set(resolve(generatedRoot, "imports.ts"), imports.facadeSource);
  const program = ts.createProgram([modulePath, entryPath], options, host);
  compilerInputs.assertValid();
  const file = program.getSourceFile(modulePath);
  if (!file) fail("unsupported-source", "Configured RPC module could not be parsed.");
  const checker = program.getTypeChecker();
  const sdkAuthoring = program.getSourceFile(resolve(sdkRoot, "authoring.d.ts"));
  const sdkSymbol = sdkAuthoring && checker.getSymbolAtLocation(sdkAuthoring);
  const rpcSymbol = sdkSymbol && checker.getExportsOfModule(sdkSymbol).find((symbol) => symbol.name === "rpc");
  if (!rpcSymbol) fail("unsupported-source", "The resolved SDK does not expose the frozen rpc authoring surface.");
  const importFile = program.getSourceFile(resolve(generatedRoot, "imports.ts"));
  const importSymbol = importFile && checker.getSymbolAtLocation(importFile);
  const rpcImportsSymbol = importSymbol && checker.getExportsOfModule(importSymbol).find(symbol => symbol.name === "rpcImports");
  const moduleSymbol = checker.getSymbolAtLocation(file);
  let exported = moduleSymbol && checker.getExportsOfModule(moduleSymbol).find((symbol) => symbol.name === rootConfig.export);
  if (exported && exported.flags & ts.SymbolFlags.Alias) exported = checker.getAliasedSymbol(exported);
  const declaration = exported?.valueDeclaration;
  if (!declaration || !ts.isVariableDeclaration(declaration) || !ts.isIdentifier(declaration.name) || declaration.getSourceFile() !== file) {
    fail("unsupported-source", "rpc.export must select one local exported const router binding.");
  }
  const exportName = declaration.name.text;
  if (file.text.includes("$sunderOwnedRpcBind")) fail("generated-name-collision", "Source uses a reserved generated binding name.");
  let nodes = 0;
  let sourceBytesVisited = 0;
  for (const sourceFile of program.getSourceFiles()) {
    if (sourceFile.isDeclarationFile || overlayPath(sourceFile.fileName) !== undefined) continue;
    sourceBytesVisited += Buffer.byteLength(sourceFile.text.replaceAll("\r\n", "\n"));
    if (sourceBytesVisited > MAX_SOURCE_BYTES) fail("source-limit-exceeded", "RPC source graph exceeds 4 MiB.");
    const visit = (node: ts.Node): void => {
      if (++nodes > 65_536) fail("source-limit-exceeded", "RPC source graph exceeds 65,536 syntax/type nodes.");
      ts.forEachChild(node, visit);
    };
    visit(sourceFile);
  }
  const active = new Set<ts.Symbol>();
  const constants = new Map<ts.Symbol, Value>();
  const metadataReferences = new Set<ts.Identifier>();
  const handlerNodes = new Set<ts.Node>();
  const objectNodes = new WeakMap<Obj, ts.ObjectLiteralExpression>();
  const arrayNodes = new WeakMap<Value[], ts.ArrayLiteralExpression>();
  const at = (node: ts.Node, message: string): never => {
    const position = file.getLineAndCharacterOfPosition(node.getStart(file));
    return fail("unsupported-source", `${rootConfig.module}:${position.line + 1}:${position.character + 1}: ${message}`);
  };
  function importedCall(node: ts.CallExpression): string | undefined {
    const callee = node.expression;
    if (!ts.isPropertyAccessExpression(callee) || callee.name.text !== "provider" || !ts.isPropertyAccessExpression(callee.expression)
      || !ts.isIdentifier(callee.expression.expression)) return undefined;
    const receiver = callee.expression.expression; const symbol = checker.getSymbolAtLocation(receiver);
    const specifier = symbol?.declarations?.[0];
    if (!specifier || !ts.isImportSpecifier(specifier) || specifier.isTypeOnly || specifier.parent.parent.isTypeOnly) return undefined;
    const declaration = specifier.parent.parent.parent;
    if (!ts.isImportDeclaration(declaration) || !ts.isStringLiteral(declaration.moduleSpecifier) || declaration.moduleSpecifier.text !== "#sunder/rpc"
      || checker.getAliasedSymbol(symbol!) !== rpcImportsSymbol) return undefined;
    const key = callee.expression.name.text;
    if (!imports.providerSurfaces.has(key)) fail("provider-invalid", `No restored provider-import surface exists for '${key}'. Run api implement first.`);
    metadataReferences.add(receiver); return key;
  }
  function evaluate(node: ts.Node, depth = 0): Value {
    if (++nodes > 65_536 || depth > 64) fail("source-limit-exceeded", "RPC syntax/reference budget exceeded.");
    const next = (child: ts.Node): Value => evaluate(child, depth + 1);
    if (ts.isParenthesizedExpression(node) || ts.isSatisfiesExpression(node)) return next(node.expression);
    if (ts.isAsExpression(node) && node.type.getText(file) === "const") return next(node.expression);
    if (ts.isIdentifier(node)) {
      metadataReferences.add(node);
      let symbol = checker.getSymbolAtLocation(node);
      if (!symbol) return at(node, "Unresolved metadata symbol.");
      if (symbol.flags & ts.SymbolFlags.Alias) {
        const imported = symbol.declarations?.[0];
        const importDeclaration = imported && ts.isImportSpecifier(imported) ? imported.parent.parent.parent : undefined;
        if (!importDeclaration || !ts.isImportDeclaration(importDeclaration) || !ts.isStringLiteral(importDeclaration.moduleSpecifier)
          || importDeclaration.moduleSpecifier.text !== "@sunder/sdk") return at(node, "Metadata constructors require direct SDK imports, not package or local reexports.");
        symbol = checker.getAliasedSymbol(symbol);
      }
      if (symbol === rpcSymbol) return new Builtin("rpc");
      if (active.has(symbol)) return at(node, "Cyclic metadata constant.");
      if (constants.has(symbol)) return constants.get(symbol)!;
      const decl = symbol.valueDeclaration;
      if (!decl || !ts.isVariableDeclaration(decl) || decl.getSourceFile() !== file || !ts.isIdentifier(decl.name)
        || !ts.isVariableDeclarationList(decl.parent) || !(decl.parent.flags & ts.NodeFlags.Const) || !decl.initializer) {
        return at(node, "Metadata references must be local const bindings or the resolved SDK rpc export.");
      }
      if (constants.size + active.size >= 4096) fail("source-limit-exceeded", "RPC constant budget exceeded.");
      active.add(symbol);
      const value = next(decl.initializer);
      active.delete(symbol);
      constants.set(symbol, value);
      return value;
    }
    if (ts.isPropertyAccessExpression(node)) {
      const receiver = next(node.expression);
      if (!(receiver instanceof Builtin)) return at(node, "Only direct members of SDK RPC constructors are metadata.");
      return new Builtin(`${receiver.name}.${node.name.text}`);
    }
    if (ts.isCallExpression(node)) {
      const importKey = importedCall(node);
      if (importKey !== undefined) return new Call("rpcImports.provider", node.arguments.map(next), importKey);
      const callee = next(node.expression);
      if (!(callee instanceof Builtin) || !CONSTRUCTORS.has(callee.name)) return at(node, "Only closed SDK RPC constructor calls are allowed.");
      return new Call(callee.name, node.arguments.map(next));
    }
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) return node.text;
    if (ts.isNumericLiteral(node)) return Number(node.text);
    if (ts.isPrefixUnaryExpression(node) && node.operator === ts.SyntaxKind.MinusToken && ts.isNumericLiteral(node.operand)) return -Number(node.operand.text);
    if (node.kind === ts.SyntaxKind.TrueKeyword) return true;
    if (node.kind === ts.SyntaxKind.FalseKeyword) return false;
    if (node.kind === ts.SyntaxKind.NullKeyword) return null;
    if (ts.isArrayLiteralExpression(node)) {
      const values = node.elements.map(next);
      arrayNodes.set(values, node);
      return values;
    }
    if (ts.isArrowFunction(node) || ts.isFunctionExpression(node)) { handlerNodes.add(node); return new Handler(node); }
    if (ts.isObjectLiteralExpression(node)) {
      const result: Obj = Object.create(null) as Obj;
      objectNodes.set(result, node);
      const seen = new Set<string>();
      for (const prop of node.properties) {
        if (!prop.name || ts.isComputedPropertyName(prop.name) || (!ts.isIdentifier(prop.name) && !ts.isStringLiteral(prop.name))) return at(prop, "Computed keys and spreads are forbidden.");
        const key = prop.name.text;
        if (seen.has(key.toLowerCase())) fail("identity-collision", `Duplicate or ASCII-case-colliding key '${key}'.`);
        seen.add(key.toLowerCase());
        if (ts.isMethodDeclaration(prop)) { handlerNodes.add(prop); result[key] = new Handler(prop); }
        else if (ts.isPropertyAssignment(prop)) result[key] = next(prop.initializer);
        else if (ts.isShorthandPropertyAssignment(prop)) {
          metadataReferences.add(prop.name);
          const symbol = checker.getShorthandAssignmentValueSymbol(prop);
          const decl = symbol?.valueDeclaration;
          if (!decl || !ts.isVariableDeclaration(decl)) return at(prop, "Shorthand must reference a local const.");
          result[key] = next(decl.name);
        } else return at(prop, "Getters and setters are forbidden.");
      }
      return result;
    }
    return at(node, "Unsupported metadata expression; package code is never executed by RPC analysis.");
  }
  const router = call(evaluate(declaration.name), "rpc.router", 1);
  const graph = object(router[0]); only(graph, "contracts", "providers");
  const contractsInput = object(graph.contracts);
  const providersInput = object(graph.providers);
  // A const binding does not make its record immutable. Prove that metadata
  // references stay in the closed DSL/const graph, including aliases and nested
  // containers. Handler bodies are implementation, not metadata expressions.
  const references = new Map<ts.Symbol, ts.Identifier[]>();
  const collectReferences = (node: ts.Node): void => {
    if (handlerNodes.has(node) || ts.isTypeNode(node)) return;
    if (++nodes > 65_536) fail("source-limit-exceeded", "RPC metadata reference proof exceeds the syntax budget.");
    if (ts.isIdentifier(node) && !(ts.isVariableDeclaration(node.parent) && node.parent.name === node)) {
      let symbol = ts.isShorthandPropertyAssignment(node.parent) ? checker.getShorthandAssignmentValueSymbol(node.parent) : checker.getSymbolAtLocation(node);
      if (symbol?.flags && symbol.flags & ts.SymbolFlags.Alias) symbol = checker.getAliasedSymbol(symbol);
      if (symbol) {
        const uses = references.get(symbol) ?? [];
        uses.push(node);
        references.set(symbol, uses);
      }
    }
    ts.forEachChild(node, collectReferences);
  };
  collectReferences(file);
  for (const [symbol, value] of constants) {
    if (value === null || typeof value !== "object" || value instanceof Builtin || symbol.valueDeclaration === declaration) continue;
    const variable = symbol.valueDeclaration;
    if (variable && ts.isVariableDeclaration(variable) && ts.isVariableDeclarationList(variable.parent)
      && ts.isVariableStatement(variable.parent.parent) && variable.parent.parent.modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.ExportKeyword)) {
      at(variable, "RPC metadata constant has an unsafe export escape; export only the configured router.");
    }
    for (const reference of references.get(symbol) ?? []) {
      if (metadataReferences.has(reference)) continue;
      let parent: ts.Node | undefined = reference.parent;
      let safe = false;
      while (parent && !ts.isSourceFile(parent) && !ts.isFunctionLike(parent)) {
        if (ts.isVariableDeclaration(parent) && ts.isIdentifier(parent.name) && parent.initializer) {
          try {
            const alias = evaluate(parent.name);
            safe = !(alias instanceof Handler) && metadataReferences.has(reference);
          } catch { /* Non-DSL calls and property/index aliases are unsafe escapes. */ }
          break;
        }
        parent = parent.parent;
      }
      if (!safe) at(reference, "RPC metadata constant mutation or unsafe escape outside the closed DSL is not supported.");
    }
  }
  if (Object.keys(contractsInput).length > 128 || Object.keys(providersInput).length > 128) fail("source-limit-exceeded", "At most 128 contracts and providers are allowed.");
  const contracts: LoadedContract[] = [];
  const models = new Map<string, ReturnType<typeof authoringBindingModel>>();
  const ids = new Set<string>();
  const files = new Map<string, string>();
  const javascript = /\.js$/u.test(modulePath);
  const extension = javascript ? "js" : "ts";
  for (const [name, source] of imports.sources) files.set(javascript ? name.replace(/\.ts$/u, ".js") : name, javascript ? transpile(source) : source);
  for (const contract of imports.contracts) files.set(contract.descriptorPath, contract.canonical);
  const logicalNames = new Map<string, string>();
  const enumArrays = new Set<Value[]>();
  for (const key of Object.keys(contractsInput).sort()) {
    wireKey(key, "service-invalid");
    if (["adapter", "imports", "browser", "index", "source-context", "authoring-receipt", "app-authoring-receipt", "create-package-api"].includes(key)) fail("generated-name-collision", `Contract key '${key}' collides with an owned output name.`);
    const contract = object(call(contractsInput[key], "rpc.contract", 1)[0]);
    only(contract, "exposure", "contractId", "serviceId", "includeSchemaDialect", "appUse", "runtimeUse", "errors", "definitions", "services");
    const exposure = contract.exposure;
    if (exposure !== "package" && exposure !== "public") fail("service-invalid", "Contract exposure must be package or public.");
    const contractId = identity(contract.contractId ?? `${project.config.id}.${key.replaceAll("-", ".")}`);
    const includeSchemaDialect = contract.includeSchemaDialect === undefined ? false : primitive(contract.includeSchemaDialect);
    if (typeof includeSchemaDialect !== "boolean") fail("service-invalid", "includeSchemaDialect must be a literal boolean.");
    const accepted = baseline?.contracts.get(key);
    if (accepted && accepted.contractId !== contractId) fail("baseline-drift", "Source-derived contract identity differs from its stable accepted identity.");
    const contractVersion = accepted?.current?.descriptor.version ?? "1.0.0";
    // Defaults use the package namespace. An explicit canonical identity may
    // preserve an existing wire contract during migration, as in the C# frontend.
    // Registry ownership is independently verified; this grants no authority.
    if (ids.has(contractId)) fail("identity-collision", `Duplicate contract ID '${contractId}'.`);
    ids.add(contractId);
    const definitions: Record<string, Schema> = Object.create(null) as Record<string, Schema>;
    const schemaBudget: SchemaBudget = { nodes: 0, tagged: [], enumArrays };
    const convert = (value: Value | undefined): Schema => schema(value, schemaBudget);
    for (const [name, value] of Object.entries(object(contract.definitions))) {
      if (!/^[A-Z][A-Za-z0-9]*$/u.test(name)) fail("value-invalid", `Definition '${name}' must be Pascal ASCII.`);
      definitions[name] = convert(value);
    }
    const errors: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    for (const [name, value] of Object.entries(object(contract.errors))) {
      const error = object(value); only(error, "description", "details");
      errors[name] = { ...(error.description === undefined ? {} : { description: primitive(error.description) }),
        ...(error.details === undefined ? {} : { detailsSchema: convert(error.details) }) };
    }
    const services: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    const sourceServices = object(contract.services);
    if (contract.serviceId !== undefined && Object.keys(sourceServices).length !== 1) fail("service-invalid", "serviceId requires exactly one service.");
    for (const [serviceKey, value] of Object.entries(sourceServices)) {
      wireKey(serviceKey, "service-invalid");
      const serviceId = contract.serviceId ?? serviceKey;
      wireKey(serviceId, "service-invalid");
      const methods: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
      for (const [methodKey, methodValue] of Object.entries(object(value))) {
        wireKey(methodKey, "method-invalid");
        if (!(methodValue instanceof Call) || !["rpc.unary", "rpc.serverStream"].includes(methodValue.name)) fail("method-invalid", "Methods must use unary or serverStream.");
        const stream = methodValue.name === "rpc.serverStream";
        const method = object(call(methodValue, methodValue.name, 1)[0]);
        only(method, "request", stream ? "event" : "response", "idempotency", "errors");
        if (method.request === undefined || method[stream ? "event" : "response"] === undefined
          || method.idempotency !== undefined && !["none", "idempotent"].includes(String(method.idempotency))) fail("method-invalid", "Method request/output and idempotency are invalid.");
        methods[methodKey] = { kind: stream ? "server-stream" : "unary", idempotency: primitive(method.idempotency ?? "none"),
          requestSchema: convert(method.request), [stream ? "eventSchema" : "responseSchema"]: convert(stream ? method.event : method.response),
          errors: data(method.errors ?? []) };
      }
      services[String(serviceId)] = { methods };
    }
    for (const tagged of schemaBudget.tagged) {
      const tags = new Set<string>();
      for (let variant of tagged.variants) {
        const seen = new Set<Schema>();
        while (typeof variant.$ref === "string" && !seen.has(variant)) {
          seen.add(variant);
          const name = variant.$ref.slice("#/$defs/".length);
          variant = Object.hasOwn(definitions, name) ? definitions[name]! : {};
        }
        const properties = variant.properties as Record<string, Schema> | undefined;
        const tag = properties && Object.hasOwn(properties, tagged.discriminator) ? properties[tagged.discriminator]!.const : undefined;
        if (typeof tag !== "string" || !Array.isArray(variant.required) || !variant.required.includes(tagged.discriminator) || tags.has(tag)) {
          fail("value-invalid", "taggedUnion requires a distinct required constant string at the specified discriminator in every record variant.");
        }
        tags.add(tag);
      }
    }
    let descriptor;
    try { descriptor = parseRpcContractDescriptor(JSON.stringify({ ...(includeSchemaDialect ? { $schema: "https://json-schema.org/draft/2020-12/schema" } : {}), descriptorVersion: 1, schemaProfile: "sunder.rpc.values.v1", contractId, version: contractVersion, errors, $defs: definitions, services })); }
    catch (error) { fail("value-invalid", error instanceof Error ? error.message : String(error)); }
    const usePolicies: Partial<Record<"appUse" | "runtimeUse", NonNullable<LoadedContract["appUse"]>>> = {};
    for (const field of ["appUse", "runtimeUse"] as const) {
      if (!Object.hasOwn(contract, field)) continue;
      const role = field === "appUse" ? "app" : "runtime";
      const policy = object(contract[field]); only(policy, "required", "actions");
      const required = policy.required === undefined ? false : primitive(policy.required);
      if (typeof required !== "boolean") fail(`${role}-use-invalid`, "Consumer required policy must be a literal boolean.");
      const applicable = applicableUseActions({ descriptor });
      let actions = applicable;
      if (Object.hasOwn(policy, "actions")) {
        if (!Array.isArray(policy.actions) || policy.actions.length < 1 || policy.actions.length > 3
          || policy.actions.some(action => typeof action !== "string" || !applicable.includes(action as typeof applicable[number]))
          || new Set(policy.actions).size !== policy.actions.length) fail(`${role}-use-invalid`, "Consumer actions must be a nonempty, distinct subset of applicable discover/invoke/subscribe actions.");
        const selected = policy.actions;
        actions = applicable.filter(action => selected.includes(action));
      }
      usePolicies[field] = { required, actions };
    }
    const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor));
    const descriptorSha256 = sha(canonicalBytes);
    const abiSha256 = await computeRpcAbiSha256(descriptor);
    const model = authoringBindingModel(descriptor);
    models.set(key, model);
    const logicalName = key.split("-").map((part) => part[0]!.toUpperCase() + part.slice(1)).join("");
    if ([...logicalNames.values()].some((name) => name.toLowerCase() === logicalName.toLowerCase())) fail("generated-name-collision", `Contract binding '${logicalName}' collides.`);
    logicalNames.set(key, logicalName);
    const generated = generateTypeScriptBindings(descriptor, { descriptorSha256, abiSha256, strictNames: true, valueModuleSpecifier: "@sunder/sdk/browser" });
    virtualFiles.set(resolve(generatedRoot, `${key}.ts`), generated);
    files.set(`${key}.${extension}`, javascript ? transpile(generated) : generated);
    files.set(`contracts/${key}.rpc.json`, canonicalBytes.toString("utf8"));
    contracts.push({ contractKey: key, configuredPath: rootConfig.module, sourcePath: modulePath, descriptorPath: `contracts/${key}.rpc.json`, descriptor,
      canonicalBytes, canonical: canonicalBytes.toString("utf8"), descriptorSha256, abiSha256, exposure, ...usePolicies });
  }
  if (contracts.length + imports.contracts.length > 128 || contracts.some(contract => imports.contracts.some(imported =>
    imported.contractKey!.toLowerCase() === contract.contractKey!.toLowerCase() || imported.descriptor.contractId === contract.descriptor.contractId)))
    fail("identity-collision", "Owned and imported contracts collide or exceed the aggregate limit.");
  const providers: ProviderConfig[] = [];
  const importedProviders: ImportedProviderBinding[] = [];
  const receiptProviders: Array<{ providerId: string; role: "runtime"; audience: "owner" | "public"; contractKey: string }> = [];
  const adapterImports: string[] = [];
  const adapterRegistrations: string[] = [];
  const contextualEdits = new Map<ts.ObjectLiteralExpression, {
    start: number; end: number;
    contexts: Array<{ contractKey: string; type: string; expression: ts.Expression }>;
  }>();
  const handlerTypes: string[] = [];
  const providerTypes: string[] = [];
  const runtimeProjection: Array<{ key: string; contract: string; importKey?: string; id: string; audience: string; defaultId: string; defaultAudience: string; explicitId: boolean; services: Record<string, string[]> }> = [];
  const stableHandlers: string[] = [];
  const declarations = Object.entries(providersInput).map(([providerKey, value]) => {
    const importKey = value instanceof Call ? value.importKey : undefined;
    const provider = object(call(value, importKey === undefined ? "rpc.provider" : "rpcImports.provider", 1)[0]);
    only(provider, ...(importKey === undefined ? ["contract"] : []), "id", "audience", "handlers");
    const key = importKey === undefined ? primitive(provider.contract) : imports.providerSurfaces.get(importKey)!.contractKey!;
    if (typeof key !== "string" || importKey === undefined && !Object.hasOwn(contractsInput, key)) fail("provider-invalid", "Provider contract must name a local owned contract key or a direct generated import builder.");
    const contract = importKey === undefined ? contracts.find(item => item.contractKey === key)! : imports.providerSurfaces.get(importKey)!;
    return { providerKey, importKey, provider, key, contract };
  });
  for (const { providerKey, importKey, provider, key, contract } of declarations) {
    if (providerKey.length > 128) fail("source-limit-exceeded", "Provider local key exceeds 128 characters.");
    if (!/^[a-z][A-Za-z0-9]*$/u.test(providerKey)) fail("provider-invalid", `Provider local key '${providerKey}' must be camel ASCII.`);
    const model = models.get(key) ?? authoringBindingModel(contract.descriptor);
    const id = identity(provider.id ?? `${contract.descriptor.contractId}.provider`);
    const audience = provider.audience ?? (contract.exposure === "package" ? "owner" : "public");
    if ((audience !== "owner" && audience !== "public") || (audience === "public" && contract.exposure !== "public")) fail("provider-invalid", "Provider audience is incompatible with contract exposure.");
    if (providers.some((item) => item.providerId === id)) fail("identity-collision", `Duplicate provider '${id}'.`);
    const peers = declarations.filter(item => item.contract.descriptor.contractId === contract.descriptor.contractId);
    if (peers.length > 1 && peers.some(item => item.provider.id === undefined)) fail("provider-invalid", "All bindings of a multiply provided contract require explicit IDs.");
    const handlers = object(provider.handlers);
    const sourceContract = importKey === undefined ? object(call(contractsInput[key], "rpc.contract", 1)[0]) : undefined;
    const serviceKeys = sourceContract ? Object.keys(object(sourceContract.services)) : model.services.map(service => service.descriptor.serviceId);
    exactKeys(handlers, serviceKeys);
    const alias = `$rpc${adapterImports.length}`;
    const bindingPath = importKey === undefined ? key : `imported/${key}`;
    adapterImports.push(`import * as ${alias} from "./${bindingPath}.js";`);
    const adapters: string[] = [];
    const serviceTypes: string[] = [];
    for (const service of model.services) {
      const serviceKey = sourceContract?.serviceId === undefined ? service.descriptor.serviceId : serviceKeys[0]!;
      const methods = object(handlers[serviceKey]);
      exactKeys(methods, service.methods.map((method) => method.descriptor.methodId));
      const methodAdapters: string[] = [];
      const methodTypes: string[] = [];
      for (const method of service.methods) {
        const handler = methods[method.descriptor.methodId];
        if (!(handler instanceof Handler)) fail("handler-incomplete", "Handlers must be statically recorded functions.");
        const access = `providers[${JSON.stringify(providerKey)}].handlers[${JSON.stringify(serviceKey)}][${JSON.stringify(method.descriptor.methodId)}]`;
        const handlerName = `$handler${stableHandlers.length}`;
        stableHandlers.push(`const ${handlerName} = ${access}.bind(providers[${JSON.stringify(providerKey)}].handlers[${JSON.stringify(serviceKey)}]);`);
        const invocation = `${handlerName}(request, context)`;
        methodAdapters.push(`${JSON.stringify(method.methodName)}: (request, context) => ${method.descriptor.kind === "unary" ? `Promise.resolve(${invocation})` : invocation}`);
        const requestRef = method.descriptor.requestSchema;
        const outputRef = method.descriptor.kind === "unary" ? method.descriptor.responseSchema! : method.descriptor.eventSchema!;
        const typeName = (ref: typeof requestRef): string => `import("./${bindingPath}.js").${model.definitionNames.get((typeof ref === "string" ? ref : ref.$ref).slice("#/$defs/".length))!}`;
        methodTypes.push(`${JSON.stringify(method.descriptor.methodId)}: (request: ${typeName(requestRef)}, context: import("@sunder/sdk").RpcInvocationContext) => ${method.descriptor.kind === "unary" ? `${typeName(outputRef)} | Promise<${typeName(outputRef)}>` : `AsyncIterable<${typeName(outputRef)}>`}`);
      }
      adapters.push(`{ ${methodAdapters.join(", ")} }`);
      serviceTypes.push(`${JSON.stringify(serviceKey)}: { ${methodTypes.join("; ")} }`);
    }
    adapterRegistrations.push(`${alias}.${model.providerRegistrationFactoryName}(${JSON.stringify(id)}, ${adapters.join(", ")})`);
    const handlerNode = objectNodes.get(handlers)!;
    const type = `Handlers${handlerTypes.length}`;
    handlerTypes.push(`export type ${type} = { ${serviceTypes.join("; ")} };`);
    providerTypes.push(`${JSON.stringify(providerKey)}: { ${importKey === undefined ? "contract" : "importKey"}: string; id?: string; audience?: string; handlers: import("./source-context").${type} }`);
    const handlersProperty = objectNodes.get(provider)!.properties.find((property) => property.name
      && (ts.isIdentifier(property.name) || ts.isStringLiteral(property.name)) && property.name.text === "handlers")!;
    const expression = ts.isPropertyAssignment(handlersProperty) ? handlersProperty.initializer
      : ts.isShorthandPropertyAssignment(handlersProperty) ? handlersProperty.name
        : fail("handler-incomplete", "Provider handlers must be a recorded expression.");
    const edit = contextualEdits.get(handlerNode) ?? { start: handlerNode.getStart(file), end: handlerNode.end, contexts: [] };
    edit.contexts.push({ contractKey: key, type, expression });
    contextualEdits.set(handlerNode, edit);
    const registration: ProviderConfig = { providerId: id, role: "runtime", contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version, audience };
    providers.push(registration);
    if (importKey !== undefined) importedProviders.push({ key: importKey, provider: registration });
    receiptProviders.push({ providerId: id, role: "runtime", audience, contractKey: key });
    runtimeProjection.push({ key: providerKey, contract: key, ...(importKey === undefined ? {} : { importKey }), id, audience, defaultId: `${contract.descriptor.contractId}.provider`,
      defaultAudience: contract.exposure === "package" ? "owner" : "public", explicitId: peers.length > 1,
      services: Object.fromEntries(serviceKeys.map((service) => [service, Object.keys(object(handlers[service])).sort()])) });
  }
  for (const key of imports.providerSurfaces.keys()) if (!importedProviders.some(binding => binding.key === key) && !analysisOptions.allowUnboundImportedProviderKeys?.has(key))
    fail("handler-incomplete", `Provider import '${key}' has no local implementation. Bind rpcImports.${key}.provider(...) or run api unimplement.`);
  providers.sort((a, b) => ordinal(a.providerId, b.providerId));
  importedProviders.sort((a, b) => ordinal(a.provider.providerId, b.provider.providerId));
  receiptProviders.sort((a, b) => ordinal(a.providerId, b.providerId));
  const runtimeContracts = contracts.map((contract) => { const key = contract.contractKey!; return ({ key,
    contractId: contract.descriptor.contractId, defaultId: `${project.config.id}.${key.replaceAll("-", ".")}`, exposure: contract.exposure,
    serviceId: object(call(contractsInput[key], "rpc.contract", 1)[0]).serviceId,
    includeSchemaDialect: contract.descriptor.$schema !== undefined,
    appUse: contract.appUse,
    runtimeUse: contract.runtimeUse,
    defaultUseActions: contract.appUse === undefined && contract.runtimeUse === undefined ? undefined : applicableUseActions(contract),
    services: Object.fromEntries(Object.entries(object(object(call(contractsInput[key], "rpc.contract", 1)[0]).services)).map(([service, methods]) => [service, Object.keys(object(methods)).sort()])),
  }); });
  const adapter = `${adapterImports.join("\n")}
function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("RPC runtime provider projection mismatch.");
  const descriptors = Object.getOwnPropertyDescriptors(value);
  if (Reflect.ownKeys(value).length !== Object.keys(descriptors).length || Object.values(descriptors).some((item) => !("value" in item))) throw new Error("RPC runtime provider projection mismatch.");
  return value as Record<string, unknown>;
}
function keys(value: Record<string, unknown>, expected: readonly string[]): void {
  if (JSON.stringify(Object.getOwnPropertyNames(value).sort()) !== JSON.stringify([...expected].sort())) throw new Error("RPC runtime provider projection mismatch.");
}
export function bind(router: unknown): void {
  const contracts = record((router as { contracts: unknown }).contracts);
  type UsePolicy = { required: boolean; actions: readonly string[] };
  const contractProjection: readonly { key: string; contractId: string; defaultId: string; exposure: string; serviceId?: string; includeSchemaDialect: boolean; appUse?: UsePolicy; runtimeUse?: UsePolicy; defaultUseActions?: readonly string[]; services: Readonly<Record<string, readonly string[]>> }[] = ${JSON.stringify(runtimeContracts)};
  keys(contracts, contractProjection.map((item) => item.key));
  for (const item of contractProjection) {
    const contract = record(contracts[item.key]);
    if (contract.exposure !== item.exposure || contract.serviceId !== item.serviceId
       || (contract.includeSchemaDialect === undefined ? false : contract.includeSchemaDialect) !== item.includeSchemaDialect
       || (contract.contractId === undefined ? item.defaultId : contract.contractId) !== item.contractId) throw new Error("RPC runtime provider projection mismatch.");
    for (const field of ["appUse", "runtimeUse"] as const) {
      const expectedPolicy = item[field];
      const message = "RPC runtime " + (field === "appUse" ? "App" : "Runtime") + " policy mismatch.";
      if ((contract[field] === undefined) !== (expectedPolicy === undefined)) throw new Error(message);
      if (expectedPolicy === undefined) continue;
      const policy = record(contract[field]);
      keys(policy, ["required", "actions"].filter(key => Object.hasOwn(policy, key)));
      if ((policy.required === undefined ? false : policy.required) !== expectedPolicy.required) throw new Error(message);
      const actions = policy.actions === undefined ? item.defaultUseActions : policy.actions;
      if (!Array.isArray(actions) || actions.length !== expectedPolicy.actions.length
          || new Set(actions).size !== actions.length || actions.some(action => !expectedPolicy.actions.includes(action)))
        throw new Error(message);
    }
    const services = record(contract.services);
    keys(services, Object.keys(item.services));
    for (const [service, methods] of Object.entries(item.services)) keys(record(services[service]), methods);
  }
  (router as { [key: symbol]: (factory: (providers: { ${providerTypes.join("; ")} }) => readonly import("@sunder/sdk").RpcProviderRegistration[]) => void })[Symbol.for("sunder.rpc.owned-provider-bind.v1")]((providers) => {
    const expected: readonly { key: string; contract: string; importKey?: string; id: string; audience: string; defaultId: string; defaultAudience: string; explicitId: boolean; services: Readonly<Record<string, readonly string[]>> }[] = ${JSON.stringify(runtimeProjection)};
    const actual = record(providers);
    keys(actual, expected.map((item) => item.key));
    for (const item of expected) {
      const provider = record(actual[item.key]);
      keys(provider, [item.importKey === undefined ? "contract" : "importKey", "handlers", ...["id", "audience"].filter((key) => Object.hasOwn(provider, key))]);
      if ((item.importKey === undefined ? provider.contract !== item.contract : provider.importKey !== item.importKey) || (item.explicitId && provider.id === undefined)
        || (provider.id === undefined ? item.defaultId : provider.id) !== item.id
        || (provider.audience === undefined ? item.defaultAudience : provider.audience) !== item.audience) throw new Error("RPC runtime provider projection mismatch.");
      const handlers = record(provider.handlers);
      keys(handlers, Object.keys(item.services));
      for (const [service, methods] of Object.entries(item.services)) {
        const implementation = record(handlers[service]);
        keys(implementation, methods);
        if (methods.some((method) => typeof implementation[method] !== "function")) throw new Error("RPC runtime provider projection mismatch.");
      }
    }
    ${stableHandlers.join("\n    ")}
    return [${adapterRegistrations.join(",\n    ")}];
  });
}
`;
  files.set("adapter.js", transpile(adapter));
  virtualFiles.set(resolve(generatedRoot, "adapter.ts"), adapter);
  const exports = [...logicalNames].map(([key, name]) => `export * as ${name} from "./${key}.js";`).join("\n");
  const importSource = `// <auto-generated />\n${imports.facadeSource}\n${exports}\n`;
  files.set(`imports.${extension}`, javascript ? transpile(importSource) : importSource);
  const appUses = project.config.app === undefined ? [] : ownedAppUses(contracts);
  const runtimeUses = ownedRuntimeUses(contracts);
  const browserSource = `// <auto-generated />\n${exports}\n${project.config.app === undefined ? "" : ownedAppClientSource(project.config.id, contracts)}`;
  files.set(`browser.${extension}`, javascript ? transpile(browserSource) : browserSource);
  files.set("index.d.ts", `${exports}\nexport { packageApis, rpcImports } from "./imports.js";\n`);
  virtualFiles.set(resolve(generatedRoot, "imports.ts"), importSource);
  virtualFiles.set(resolve(generatedRoot, "browser.ts"), browserSource);
  virtualFiles.set(resolve(generatedRoot, "index.d.ts"), files.get("index.d.ts")!);

  // Contextual types come from the codec backend, not an any-typed DSL stub.
  // The original source is untouched; only this compiler host sees the wrappers.
  const checkHost = ts.createCompilerHost(options);
  checkHost.realpath = host.realpath;
  checkHost.getDirectories = host.getDirectories;
  const contextTypesPath = resolve(generatedRoot, "source-context.ts");
  virtualFiles.set(contextTypesPath, `${handlerTypes.join("\n")}\n`);
  files.set("source-context.d.ts", `${handlerTypes.join("\n")}\n`);
  const contextType = (type: string): string => `import(${JSON.stringify(contextTypesPath.replace(/\.ts$/u, ".js"))}).${type}`;
  const checkingEdits = [...contextualEdits.values()].map((edit) => {
    // Contextual inference needs one concrete context, independently of source
    // enumeration. It is NOT proof of all shared bindings; those are checked
    // separately below, using the actual source expressions rather than the
    // router's potentially broader SDK declaration types.
    const context = [...edit.contexts].sort((a, b) => ordinal(a.contractKey, b.contractKey))[0]!;
    const type = contextType(context.type);
    return { start: edit.start, end: edit.end, prefix: javascript ? `/** @satisfies {${type}} */ (` : "(", suffix: javascript ? ")" : ` satisfies ${type})` };
  });
  // These arrays have passed metadata escape/mutation checks and strict enum
  // validation. Preserve their literal elements for checking without changing
  // the authored source or the source snapshot supplied to the runtime bundler.
  for (const values of enumArrays) {
    const array = arrayNodes.get(values)!;
    let initializer: ts.Expression = array;
    while (ts.isParenthesizedExpression(initializer.parent) || ts.isAsExpression(initializer.parent) || ts.isSatisfiesExpression(initializer.parent)) initializer = initializer.parent;
    if (!ts.isVariableDeclaration(initializer.parent) || initializer.parent.initializer !== initializer) continue;
    if (javascript && ts.getJSDocType(initializer)?.getText(file) === "const") continue;
    const tuple = `[${values.map((value) => JSON.stringify(value).replaceAll("*/", "*\\u002f")).join(", ")}]`;
    checkingEdits.push({ start: initializer.getStart(file), end: initializer.end,
      prefix: javascript ? `/** @type {${tuple}} */ (` : "(", suffix: javascript ? ")" : ` as ${tuple})` });
  }
  // Retain authored type constraints while narrowing explicitly typed aliases.
  // Mutable array annotations remain assignable; mutation is rejected by the
  // earlier metadata proof, not hidden by these checking-only tuple assertions.
  for (const [symbol, values] of constants) {
    if (!Array.isArray(values) || !enumArrays.has(values)) continue;
    const variable = symbol.valueDeclaration;
    if (!variable || !ts.isVariableDeclaration(variable)) continue;
    const annotation = variable.type ?? ts.getJSDocType(variable);
    if (!annotation) continue;
    const tuple = `readonly [${values.map((value) => JSON.stringify(value).replaceAll("*/", "*\\u002f")).join(", ")}]`;
    checkingEdits.push({ start: annotation.getStart(file), end: annotation.end, prefix: "(", suffix: `) & ${tuple}` });
  }
  let contextualSource = file.text;
  for (const edit of checkingEdits.sort((a, b) => b.start - a.start)) {
    const expression = contextualSource.slice(edit.start, edit.end);
    const wrapped = edit.prefix + expression + edit.suffix;
    contextualSource = contextualSource.slice(0, edit.start) + wrapped + contextualSource.slice(edit.end);
  }
  for (const edit of contextualEdits.values()) if (edit.contexts.length > 1) for (const context of edit.contexts) {
    const expression = context.expression.getText(file); const type = contextType(context.type);
    contextualSource += javascript ? `\nvoid (/** @satisfies {${type}} */ (${expression}));\n`
      : `\nvoid ((${expression}) satisfies ${type});\n`;
  }
  checkHost.readFile = (path) => resolve(path) === modulePath || resolve(path) === canonicalModulePath ? contextualSource : host.readFile(path);
  checkHost.fileExists = host.fileExists;
  checkHost.directoryExists = host.directoryExists;
  const runtime = await captureOwnedRuntimeInputs(project.root, entryPath, modulePath, exportName, file.text, files);
  const checked = ts.createProgram([...new Set([modulePath, entryPath, ...runtime.sourceFiles, ...virtualFiles.keys()])], options, checkHost);
  compilerInputs.assertValid();
  const diagnostics = [...checked.getOptionsDiagnostics(), ...checked.getGlobalDiagnostics(), ...checked.getSyntacticDiagnostics(), ...checked.getSemanticDiagnostics()];
  if (diagnostics.length) fail("handler-incomplete", ts.formatDiagnostics(diagnostics, { getCanonicalFileName: (path) => path, getCurrentDirectory: () => project.root, getNewLine: () => "\n" }));
  // Only generated modules participate in declaration emission. Source helpers
  // may legitimately ship both helper.js and helper.d.ts.
  const declarationProgram = ts.createProgram([...virtualFiles.keys()], { ...options, noEmit: false, declaration: true, emitDeclarationOnly: true }, checkHost);
  compilerInputs.assertValid();
  for (const path of virtualFiles.keys()) {
    if (path.endsWith(".d.ts")) continue;
    const emitted = declarationProgram.emit(declarationProgram.getSourceFile(path), (outputPath, text) => files.set(outputPath.slice(generatedRoot.length + 1), text));
    if (emitted.emitSkipped || emitted.diagnostics.length) fail("handler-incomplete", "Generated RPC declarations could not be emitted.");
  }
  for (const [name, text] of files) if (/\.[jt]s$/u.test(name)) virtualFiles.set(resolve(generatedRoot, name), text);

  const inputName = (path: string): string => {
    path = resolve(path);
    return path.startsWith(`${resolve(project.root)}/`) ? relative(project.root, path).replaceAll("\\", "/")
      : path.startsWith(`${sdkRoot}/`) ? `sdk/${relative(sdkRoot, path).replaceAll("\\", "/")}`
        : path.startsWith(`${resolve(dirname(require.resolve("typescript")))}/`) ? `typescript/${basename(path)}` : `external/${basename(path)}`;
  };
  if (baseline && !analysisOptions.allowDrift) validateBaselineSource(baseline, contracts);
  const physicalFingerprint = sha(JSON.stringify({ config: configBytes.toString("utf8"), lock: lock.toString("utf8"), physicalLock: physicalLock.toString("utf8"), baseline: baselineInput.fingerprint,
    compilerInputs: compilerInputs.fingerprint(), runtimeInputs: runtime.physicalFingerprint,
    source, entry, compiler: ts.version, producer: { toolVersion, sdkVersion, sdkVersionRange, sdkBaselineCapability, implementation: producer.fingerprint }, runtime: runtime.fingerprint,
    inputs: [...reads].map(([path, text]) => [inputName(path), text === undefined ? null : sha(text)]).sort((a, b) => ordinal(JSON.stringify(a), JSON.stringify(b))) }));
  const fingerprint = sha(JSON.stringify({ physicalFingerprint, identities: contracts.map((contract) => [contract.contractKey, contract.descriptor.contractId, contract.descriptor.version]) }));
  const allContracts = [...contracts, ...imports.contracts].sort((a, b) => ordinal(a.contractKey!, b.contractKey!));
  const receipt = {
    schemaVersion: 1, producer: { name: "@sunder/package-tool", version: toolVersion, language: javascript ? "javascript" : "typescript" },
    inputFingerprint: fingerprint, lockSha256: sha(lock), packageId: project.config.id, packageVersion: project.config.version,
    contracts: allContracts.map((contract) => ({ contractKey: contract.contractKey!, contractId: contract.descriptor.contractId,
      contractVersion: contract.descriptor.version, exposure: contract.exposure, descriptorBytes: contract.canonicalBytes.length,
      canonicalDescriptor: contract.canonical, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256,
      ...(contract.appUse === undefined ? {} : { appUse: contract.appUse }),
      ...(contract.runtimeUse === undefined ? {} : { runtimeUse: contract.runtimeUse }) })),
    packageArtifacts: importReceiptArtifacts(imports), bundles: allContracts.map(contract => contract.imported
      ? { contractKey: contract.contractKey!, source: "imported", ...authority(contract.imported) }
      : { contractKey: contract.contractKey!, source: "owned", authorityKind: "package", authorityId: project.config.id }),
    ...importProjection(imports, new Set(["runtime"]), importedProviders), providers: receiptProviders,
    uses: [...imports.uses.map(use => ({ role: use.role, required: use.required, actions: use.actions, contractKey: use.contractId })),
      ...runtimeUses.map(use => ({ role: use.role, required: use.required, actions: use.actions,
        contractKey: contracts.find(contract => contract.descriptor.contractId === use.contractId)!.contractKey! }))]
      .sort((a, b) => ordinal(a.contractKey, b.contractKey)),
    generatedPackageFacades: importedFacadeReceipt(imports),
    generatedBindings: allContracts.map(contract => ({ contractKey: contract.contractKey!, logicalName: contract.imported ? contractSymbol(contract.contractKey!) : logicalNames.get(contract.contractKey!)! })),
  };
  const receiptText = `${JSON.stringify(receipt, null, 2)}\n`;
  if (Buffer.byteLength(receiptText) > 67_108_864) fail("aggregate-materializability-overflow", "RPC authoring receipt exceeds 64 MiB.");
  files.set("authoring-receipt.v1.json", receiptText);
  if (project.config.app !== undefined) files.set("app-authoring-receipt.v1.json", `${JSON.stringify({ ...receipt, role: "app", providers: [],
    contracts: receipt.contracts.filter(contract => contracts.some(owned => owned.contractKey === contract.contractKey))
      .map(({ runtimeUse: _runtimeUse, ...contract }) => contract),
    bundles: receipt.bundles.filter(bundle => bundle.source === "owned"), packageArtifacts: [], ...importProjection(imports, new Set(["app"])),
    generatedPackageFacades: [], generatedBindings: receipt.generatedBindings.filter(binding => logicalNames.has(binding.contractKey)),
    uses: appUses.map((use) => ({ role: "app", required: use.required, actions: use.actions,
      contractKey: contracts.find((contract) => contract.descriptor.contractId === use.contractId)!.contractKey! })) }, null, 2)}\n`);
  files.set("owned-baseline.sha256", `${baselineInput.fingerprint}\n`);
  const revalidate = async (): Promise<void> => {
    producer.revalidate();
    await baselineInput.revalidate();
    await verifyBaselineEvidence();
    for (const [path, expected] of [[project.configPath, configBytes], [lockPath, physicalLock]] as const) {
      const actual = await readProjectDescriptorFile(project.root, path, "RPC input", path === lockPath ? 33_554_432 : MAX_SOURCE_BYTES);
      if (!actual.equals(expected)) fail("unsupported-source", "RPC inputs changed while waiting/building; retry generation.");
    }
    compilerInputs.revalidate();
    for (const [path, exists] of probes) if (baseExists(path) !== exists) fail("unsupported-source", `RPC input set changed: ${path}`);
    for (const [path, exists] of directoryProbes) if (baseDirectoryExists(path) !== exists) fail("unsupported-source", `RPC input directory set changed: ${path}`);
    for (const [path, list] of directories) if (JSON.stringify(baseDirectories(path).sort()) !== list) fail("unsupported-source", `RPC resolution directory changed: ${path}`);
    await runtime.revalidate();
    producer.revalidate();
  };
  await revalidate();
  return { producerFiles: producer.files, imports, baselineInput, ...(baseline ? { baseline } : {}), physicalFingerprint, modulePath, exportName, source: file.text, files, contracts, providers, importedProviders, appUses, runtimeUses, inputFingerprint: fingerprint, runtime, revalidate };
}

const SCHEMAS = ["required", "optional", "nullable", "ref", "object", "map", "array", "string", "stringEnum", "integer", "integerEnum", "float32", "float64", "boolean", "uuid", "date", "offsetDateTime", "duration", "uri", "bigInteger", "decimal", "bytes", "contentReference", "json", "literal", "union", "taggedUnion"];
const CONSTRUCTORS = new Set(["router", "contract", "provider", "unary", "serverStream"].map((name) => `rpc.${name}`).concat(SCHEMAS.map((name) => `rpc.schema.${name}`)));
interface SchemaBudget { nodes: number; tagged: Array<{ discriminator: string; variants: Schema[] }>; enumArrays: Set<Value[]> }
function schema(value: Value | undefined, budget: SchemaBudget, depth = 0): Schema {
  if (++budget.nodes > 4096 || depth > 64) fail("source-limit-exceeded", "Derived schema node/depth budget exceeded.");
  const inner = (child: Value | undefined): Schema => schema(child, budget, depth + 1);
  if (!(value instanceof Call) || !value.name.startsWith("rpc.schema.")) fail("value-invalid", "Expected an SDK schema constructor.");
  const name = value.name.slice("rpc.schema.".length);
  const args = value.args;
  const facetIndex = ["object", "map", "array", "stringEnum", "integerEnum", "union"].includes(name) ? 1 : name === "taggedUnion" ? 2 : 0;
  const noFacets = ["required", "optional", "nullable", "ref", "literal"].includes(name);
  const required = noFacets ? 1 : facetIndex;
  if (args.length < required || args.length > (noFacets ? 1 : facetIndex + 1)) fail("value-invalid", `Invalid ${name} arity.`);
  const facets = noFacets || args[facetIndex] === undefined ? {} : object(args[facetIndex]);
  const annotations = ["title", "description"];
  const allowed = name === "json" ? ["objectOnly", "minProperties", "maxProperties"] : name === "object" ? ["minProperties", "maxProperties"] : name === "map" ? ["minProperties", "maxProperties", "minLength", "maxLength"] : name === "array" ? ["minItems", "maxItems"] : name === "bytes" ? ["maxDecodedBytes"] : name === "bigInteger" ? ["maxDigits", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"] : name === "decimal" ? ["maxPrecision", "maxScale", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"] : ["integer", "integerEnum", "float32", "float64"].includes(name) ? ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"] : ["string", "stringEnum", "uuid", "date", "offsetDateTime", "duration", "uri"].includes(name) ? ["minLength", "maxLength"] : [];
  only(facets, ...annotations, ...allowed);
  const f = Object.fromEntries(Object.entries(facets).map(([key, item]) => [(["bigInteger", "decimal"].includes(name) && ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"].includes(key)) ? `value${key[0]!.toUpperCase()}${key.slice(1)}` : key, primitive(item)]));
  if (name === "ref") return { $ref: `#/$defs/${String(primitive(args[0]))}` };
  if (name === "nullable") return { oneOf: [{ type: "null" }, inner(args[0])] };
  if (name === "object") {
    const properties: Record<string, Schema> = Object.create(null) as Record<string, Schema>;
    const requiredKeys: string[] = [];
    for (const [key, prop] of Object.entries(object(args[0]))) {
      if (!/^[a-z][A-Za-z0-9]*$/u.test(key)) fail("value-invalid", `Property '${key}' must be camel ASCII.`);
      if (!(prop instanceof Call) || !["rpc.schema.required", "rpc.schema.optional"].includes(prop.name)) fail("value-invalid", "Every object member must specify required or optional presence.");
      call(prop, prop.name, 1);
      if (prop.name.endsWith(".required")) requiredKeys.push(key);
      properties[key] = inner(prop.args[0]);
    }
    return { type: "object", properties, required: requiredKeys, additionalProperties: false, ...f };
  }
  if (name === "map") {
    const { minLength, maxLength, ...rest } = f;
    return { type: "object", additionalProperties: inner(args[0]), propertyNames: { type: "string", ...(minLength === undefined ? {} : { minLength }), ...(maxLength === undefined ? {} : { maxLength }) }, ...rest };
  }
  if (name === "array") return { type: "array", items: inner(args[0]), ...f };
  if (name === "literal") {
    const literal = primitive(args[0]);
    return { type: literal === null ? "null" : typeof literal === "number" ? Number.isSafeInteger(literal) ? "integer" : "number" : typeof literal,
      ...(typeof literal === "number" && !Number.isSafeInteger(literal) ? { format: "float64" } : {}), const: literal };
  }
  if (name === "union" || name === "taggedUnion") {
    const variants = args[name === "union" ? 0 : 1];
    if (!Array.isArray(variants)) fail("value-invalid", "Union variants must be an array.");
    if (name === "taggedUnion" && typeof args[0] !== "string") fail("value-invalid", "Discriminator must be a property name.");
    // The V1 descriptor represents discriminators through required constant members,
    // not an additional schema keyword. The strict parser validates exclusivity.
    const schemas = variants.map(inner);
    if (name === "taggedUnion") budget.tagged.push({ discriminator: args[0] as string, variants: schemas });
    return { oneOf: schemas, ...f };
  }
  if (name === "stringEnum" || name === "integerEnum") {
    const values = data(args[0]);
    if (!Array.isArray(values)) fail("value-invalid", "Enum values must be an array.");
    if (name === "integerEnum" && values.some((item) => !Number.isSafeInteger(item))) fail("value-invalid", "integerEnum requires safe integers.");
    budget.enumArrays.add(args[0] as Value[]);
    return { type: name === "stringEnum" ? "string" : "integer", enum: name === "integerEnum" ? [...values].sort((a, b) => Number(a) - Number(b)) : values,
      ...(name === "integerEnum" && values.length > 0 ? { minimum: Math.min(...values.map(Number)), maximum: Math.max(...values.map(Number)) } : {}), ...f };
  }
  if (name === "json") {
    const { objectOnly, ...jsonFacets } = f;
    if (objectOnly !== undefined && typeof objectOnly !== "boolean") fail("value-invalid", "JSON objectOnly must be a literal boolean.");
    return objectOnly === true || Object.keys(jsonFacets).some((key) => key === "minProperties" || key === "maxProperties") ? { type: "object", ...jsonFacets } : jsonFacets;
  }
  if (name === "contentReference") return { type: "object", logicalType: "content-reference", ...f };
  if (name === "bytes") return { type: "string", contentEncoding: "base64", ...f };
  if (name === "float32" || name === "float64") return { type: "number", format: name, ...f };
  if (["string", "integer", "boolean"].includes(name)) return { type: name, ...f };
  const format: Record<string, string> = { uuid: "uuid", date: "date", offsetDateTime: "date-time", duration: "duration", uri: "uri", bigInteger: "big-integer", decimal: "decimal" };
  if (Object.hasOwn(format, name)) return { type: "string", format: format[name], ...f };
  return fail("value-invalid", `Schema '${name}' is not valid at this position.`);
}
function object(value: Value | undefined): Obj {
  if (value === null || typeof value !== "object" || Array.isArray(value) || value instanceof Call || value instanceof Builtin || value instanceof Handler) fail("unsupported-source", "Expected a closed metadata record.");
  return value;
}
function only(value: Obj, ...keys: string[]): void { for (const key of Object.keys(value)) if (!keys.includes(key)) fail("unsupported-source", `Unknown metadata member '${key}'.`); }
function call(value: Value | undefined, name: string, arity: number): Value[] { if (!(value instanceof Call) || value.name !== name || value.args.length !== arity) fail("unsupported-source", `Expected ${name} with ${arity} argument(s).`); return value.args; }
function primitive(value: Value | undefined): string | number | boolean | null { if (value === undefined || typeof value === "object" && value !== null) fail("value-invalid", "Expected a primitive metadata value."); return value; }
function data(value: Value | undefined): unknown { if (!Array.isArray(value)) fail("value-invalid", "Expected a primitive metadata array."); return value.map(primitive); }
function identity(value: Value): string { if (typeof value === "string" && value.length > 128) fail("source-limit-exceeded", "Derived identity exceeds 128 characters."); if (typeof value !== "string" || !isPackageId(value)) fail("service-invalid", "Identity must be canonical package-style ASCII."); return value; }
function wireKey(value: Value, code: string): void { if (typeof value === "string" && value.length > 128) fail("source-limit-exceeded", "Local/wire key exceeds 128 characters."); if (typeof value !== "string" || !/^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/u.test(value)) fail(code, "Wire keys must already be canonical lower-kebab ASCII."); }
function exactKeys(value: Obj, expected: string[]): void { if (JSON.stringify(Object.keys(value).sort()) !== JSON.stringify([...expected].sort())) fail("handler-incomplete", "Provider handlers must implement exactly every contract service and method."); }
function transpile(source: string): string { return ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.ESNext } }).outputText; }
function ordinal(a: string, b: string): number { return a < b ? -1 : a > b ? 1 : 0; }
function sha(value: string | Uint8Array): string { return createHash("sha256").update(value).digest("hex"); }
function fail(code: string, message: string): never { throw new Error(`rpc-authoring.${code}: ${message}`); }
// TypeScript compiler hosts use slash-normalized paths on every platform.
function resolve(...paths: string[]): string { return resolveNative(...paths).replaceAll("\\", "/"); }
