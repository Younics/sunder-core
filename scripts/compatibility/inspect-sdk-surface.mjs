#!/usr/bin/env node
// Read-only review inventory. Candidate snapshots are checked against assemblies by
// PublicApiBaselineTests; this report is not a supported-release or binary receipt.
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const nodeRoot = resolve(root, "languages/typescript/node");
const sdkSource = resolve(nodeRoot, "packages/sdk/src");
const require = createRequire(resolve(nodeRoot, "package.json"));
const ts = require("typescript");
const hash = (value) => createHash("sha256").update(value).digest("hex");
const pathOf = (path) => relative(root, path).replaceAll("\\", "/");
const generatedTypes = new Set([
  "Sunder.Sdk.Rpc.ISunderRpcDeclaredErrorMetadata", "Sunder.Sdk.Rpc.SunderRpcJsonConverterFactory",
  "Sunder.Sdk.Rpc.SunderRpcMarshallingContext", "Sunder.Sdk.Rpc.SunderRpcResourceLifetime",
  "Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime", "Sunder.Sdk.Rpc.SunderRpcServiceCall",
]);
const namespaces = new Set([
  "Sunder.Sdk.Abstractions", "Sunder.Sdk.Authentication", "Sunder.Sdk.Callbacks",
  "Sunder.Sdk.Compatibility", "Sunder.Sdk.Logging", "Sunder.Sdk.Notifications",
  "Sunder.Sdk.Packaging", "Sunder.Sdk.Rpc", "Sunder.Sdk.Services", "Sunder.Sdk.Settings",
  "Sunder.Sdk.Storage", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Avalonia.Theming", "Sunder.Sdk.Stacks",
  "Sunder.Sdk.Worker", "Sunder.Package.Build.Tasks",
]);
function dotnetCategory(assembly, name) {
  if (assembly === "Sunder.Package.Build") return "build-tooling";
  if (generatedTypes.has(name)) return "generated-runtime-support";
  if (assembly === "Sunder.Sdk.Worker" || name.startsWith("Sunder.Sdk.Rpc.")
      || name.startsWith("Sunder.Sdk.Compatibility.") || /\.ISunder(?:App|Package)(?:Module|ContributionRegistry)$/u.test(name))
    return "advanced";
  return "author";
}
const dotnet = [];
for (const assembly of ["Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build"]) {
  const path = resolve(root, `languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/PublicApi/${assembly}.txt`);
  const bytes = await readFile(path);
  let current;
  const types = [];
  for (const line of bytes.toString("utf8").split("\n")) {
    if (line.startsWith("type ")) {
      const declaration = line.replace(/^type (?:public|protected(?: internal)?) (?:(?:sealed|static|abstract|readonly|ref) )*(?:class|struct|enum|interface) /u, "");
      const name = declaration.split(" : ")[0].split(" where ")[0];
      if (name === line || !namespaces.has(name.slice(0, name.lastIndexOf("."))))
        throw new Error(`Unreviewed namespace or type syntax: ${line}`);
      current = { name, category: dotnetCategory(assembly, name), members: [] };
      types.push(current);
    } else if (/^  (?:method|field|property|event) /u.test(line)) {
      if (!current) throw new Error(`Member without a declaring type: ${line}`);
      current.members.push(line.trim());
    }
  }
  dotnet.push({ assembly, input: pathOf(path), sha256: hash(bytes), types });
}

const entries = ["index", "browser", "core", "generated", "react"];
const toolEntry = resolve(nodeRoot, "packages/package-tool/src/index.ts");
const program = ts.createProgram([...entries.map((entry) => resolve(sdkSource, `${entry}.ts`)), toolEntry], {
  target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.Node16, moduleResolution: ts.ModuleResolutionKind.Node16,
  lib: ["lib.es2023.d.ts", "lib.dom.d.ts", "lib.dom.iterable.d.ts"], strict: true, noEmit: true, skipLibCheck: true, esModuleInterop: true,
});
const checker = program.getTypeChecker();
const npm = [];
for (const entry of [...entries, "package-tool"]) {
  const path = entry === "package-tool" ? toolEntry : resolve(sdkSource, `${entry}.ts`);
  const source = program.getSourceFile(path);
  const module = source && checker.getSymbolAtLocation(source);
  if (!module) throw new Error(`Missing SDK entry point: ${path}`);
  const exports = checker.getExportsOfModule(module).map((exported) => {
    const symbol = exported.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(exported) : exported;
    const declaration = symbol.getDeclarations()?.[0];
    if (!declaration) throw new Error(`Unresolved SDK export: ${exported.name}`);
    const declarationPath = pathOf(declaration.getSourceFile().fileName);
    const category = entry === "package-tool" ? "build-tooling" : entry === "generated" ? "generated-runtime-support"
      : ["runWorker", "safeStderr"].includes(exported.name) || /\/(?:descriptor(?:-v1)?|framing|resource-profile|validation)\.ts$/u.test(declarationPath)
        ? "advanced" : "author";
    const type = symbol.flags & ts.SymbolFlags.Value ? checker.getTypeOfSymbolAtLocation(symbol, declaration) : checker.getDeclaredTypeOfSymbol(symbol);
    return { name: exported.name, category, declaration: declarationPath, type: checker.typeToString(type, declaration, ts.TypeFormatFlags.NoTruncation) };
  }).sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
  npm.push({ entry: entry === "package-tool" ? "@sunder/package-tool" : entry === "index" ? "@sunder/sdk" : `@sunder/sdk/${entry}`,
    input: pathOf(path), sha256: hash(await readFile(path)), exports });
}
const ledger = JSON.parse(await readFile(resolve(root, "eng/compatibility/supported-releases.json"), "utf8"));
console.log(JSON.stringify({
  schemaVersion: 1, kind: "candidate-source-surface-review", status: ledger.status, releaseStatus: ledger.releaseStatus,
  classificationIsVisibility: false,
  note: "All public runtime and generated-support symbols remain compatibility obligations after a supported baseline; tooling internals are not runtime contracts. No release qualification is implied.",
  dotnet, npm,
  tooling: { csharp: "Sunder.Package.Build/Rpc/Generation (internal)", node: "@sunder/package-tool", cli: "sunder-rpc-gen" },
}, null, 2));
