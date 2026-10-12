import ts from "typescript";
import { createHash } from "node:crypto";
import { authoringBindingModel, isPackageId, parseRpcContractDescriptor, type RpcContractDescriptor } from "@sunder/sdk";
import type { LoadedContract } from "./model";
import type { OwnedBaseline } from "./rpc-baseline";
import { hasPublicEvidence } from "./rpc-baseline";
import { applicableUseActions, canonicalServiceType, type SourceRoleUse } from "./rpc-owned-uses";
import { emptyModelProjection, readModelProjection, modelProjectionOuter, serviceReferenceSchema, readMethodPolicies, methodPoliciesOuter, type RpcMethodPolicies, type RpcModelProjection, type RpcServiceReferenceProjection } from "./rpc-model-projection";

type Data = null | boolean | number | string | Data[] | { [key: string]: Data };
type RecordData = { [key: string]: Data };
type Schema = Record<string, unknown>;
export interface ServiceMethod {
  readonly completeOnResponseContentRetirement?: boolean;
  readonly name: string; readonly wireName: string; readonly serviceId: string;
  readonly parameters: readonly { name: string; optional: boolean }[];
  readonly control?: "signal" | "context";
  readonly stream: boolean; readonly unit: boolean; readonly directRequest?: boolean;
}
export interface SourceService {
  readonly modelProjection: RpcModelProjection;
  readonly methodPolicies?: RpcMethodPolicies;
  readonly sourceModels: ReadonlyMap<string, string>;
  readonly naturalContent:boolean;
  readonly ownerDefinition: string;
  readonly exportName: string; readonly key: string; readonly descriptor: RpcContractDescriptor;
  readonly exposure: "package" | "public"; readonly methods: readonly ServiceMethod[];
  readonly providerId?: string; readonly audience: "owner" | "public";
  readonly importKey?: string; readonly declarationOnly: boolean;
  readonly semanticOptions: unknown;
  readonly appUse?: LoadedContract["appUse"]; readonly runtimeUse?: LoadedContract["runtimeUse"];
  readonly roleUses: readonly SourceRoleUse[];
}
export function serviceKey(name: string): string {
  if (!/^[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)*$/u.test(name)) throw new Error(`rpc-authoring.service-invalid: '${name}' is not a portable source identifier.`);
  return name.replace(/([a-z0-9])([A-Z])/gu, "$1-$2").replace(/([A-Z])([A-Z][a-z])/gu, "$1-$2").replaceAll("_", "-").toLowerCase();
}
export const pascal = (name: string): string => serviceKey(name.replaceAll(".", "-")).split("-").map(part => part[0]!.toUpperCase() + part.slice(1)).join("");
export const camel = (name: string): string => { const value = pascal(name); return value[0]!.toLowerCase() + value.slice(1); };

/** The only Node service frontend. TS and checked JS share the same semantic graph. */
export function analyzeServices(program: ts.Program, file: ts.SourceFile, sdkRoot: string, owner: string, packageVersion: string,
  baseline: OwnedBaseline | undefined, imported: ReadonlyMap<string, LoadedContract>, syntaxNodes = 0,
  generated?: { readonly root: string; readonly contracts: readonly LoadedContract[] }): readonly SourceService[] {
  const checker = program.getTypeChecker();
  // One nominal table for the complete program, before any role filtering. A
  // structural type comparison can never establish source-model identity.
  const modelSymbols = new Map<ts.Symbol, { name: string; location: ts.Node; type: ts.Type; use: ts.Node }>();
  const modelNames = new Map<string, { symbol: ts.Symbol; location: ts.Node }>();
  const authoring = program.getSourceFile(`${sdkRoot}/services.d.ts`);
  const sdk = authoring && checker.getSymbolAtLocation(authoring);
  const define = sdk && checker.getExportsOfModule(sdk).find(symbol => symbol.name === "defineService");
  const errorDefinition = sdk && checker.getExportsOfModule(sdk).find(symbol => symbol.name === "defineServiceError");
  const errorTypes = new WeakMap<object, { type: ts.Type; node: ts.Node }>();
  const metadataSymbols = new Set<ts.Symbol>(); const metadataReferences = new Set<ts.Node>();
  if (!define) throw new Error("rpc-authoring.unsupported-source: The selected SDK must expose defineService; rebuild/restore the coordinated toolset.");
  let visited = syntaxNodes; const constants = new Set<ts.Symbol>(); const resolvedConstants = new Set<ts.Symbol>();
  function fail(node: ts.Node, message: string, code = "unsupported-source"): never {
    const source = node.getSourceFile(); const position = source.getLineAndCharacterOfPosition(node.getStart(source));
    throw new Error(`rpc-authoring.${code}: ${source.fileName}:${position.line + 1}:${position.character + 1}: ${message}`);
  }
  const budget = (node: ts.Node, depth: number): void => { if (++visited > 65_536 || depth > 64) fail(node, "Service syntax/type graph exceeds its node/depth budget.", "source-limit-exceeded"); };
  const symbolAt = (node: ts.Node): ts.Symbol | undefined => {
    const symbol = ts.isShorthandPropertyAssignment(node.parent) ? checker.getShorthandAssignmentValueSymbol(node.parent) : checker.getSymbolAtLocation(node);
    return symbol && symbol.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(symbol) : symbol;
  };
  const resolveMetadataExpression=(input:ts.Expression,seen=new Set<ts.Symbol>()):ts.Expression=>{
    budget(input,seen.size);
    if(ts.isParenthesizedExpression(input)||ts.isSatisfiesExpression(input)||ts.isAsExpression(input)&&input.type.getText()==="const")return resolveMetadataExpression(input.expression,seen);
    if(ts.isIdentifier(input)){
      const symbol=symbolAt(input),decl=symbol?.valueDeclaration;
      if(symbol&&decl&&ts.isVariableDeclaration(decl)&&decl.initializer&&ts.isVariableDeclarationList(decl.parent)&&decl.parent.flags&ts.NodeFlags.Const){
        if(seen.has(symbol))return fail(input,"Cyclic semantic metadata.");seen.add(symbol);resolvedConstants.add(symbol);metadataReferences.add(input);
        const result=resolveMetadataExpression(decl.initializer,seen);if(ts.isObjectLiteralExpression(result)||ts.isArrayLiteralExpression(result))metadataSymbols.add(symbol);return result;
      }
    }
    return input;
  };
  const typedRuntimeUses=(input:ts.Expression):{uses:readonly SourceRoleUse[];metadata:Data[]}=>{
    const array=resolveMetadataExpression(input);if(!ts.isArrayLiteralExpression(array)||array.elements.length<1||array.elements.length>128)return fail(input,"runtimeUses requires 1..128 explicit imported service targets.","runtime-use-invalid");
    const uses:SourceRoleUse[]=[],metadata:Data[]=[];const seen=new Set<string>();
    for(const item of array.elements){
      const recordNode=resolveMetadataExpression(item);if(!ts.isObjectLiteralExpression(recordNode))return fail(item,"Runtime use must be a literal service/policy record.","runtime-use-invalid");
      let targetNode:ts.Expression|undefined;const policy:RecordData=Object.create(null) as RecordData;const names=new Set<string>();
      for(const property of recordNode.properties){
        if(!ts.isPropertyAssignment(property)||ts.isComputedPropertyName(property.name))return fail(property,"Runtime use requires explicit data members.","runtime-use-invalid");
        const name=property.name.getText().replace(/^['"]|['"]$/gu,"");
        if(!["service","required","actions"].includes(name)||names.has(name))return fail(property,"Runtime use has an unknown/duplicate member.","runtime-use-invalid");names.add(name);
        if(name==="service")targetNode=resolveMetadataExpression(property.initializer);else policy[name]=literal(property.initializer);
      }
      if(!targetNode)return fail(item,"Runtime use requires a canonical generated service token.","runtime-use-invalid");
      let root:ts.Expression=targetNode;while(ts.isPropertyAccessExpression(root))root=root.expression;
      const rootSymbol=ts.isIdentifier(root)?symbolAt(root):undefined;
      if(rootSymbol?.name!=="Services"||!rootSymbol.declarations?.some(declaration=>generated&&declaration.getSourceFile().fileName.replaceAll("\\","/")===generated.root+"/api.ts"))return fail(targetNode,"Runtime target must come from the exact generated Services export.","runtime-use-invalid");
      const type=checker.getTypeAtLocation(targetNode);
      const identity=(name:string)=>{const property=checker.getPropertyOfType(type,name);const value=property&&checker.getTypeOfSymbolAtLocation(property,targetNode!);return value?.isStringLiteral()?value.value:undefined;};
      const contractId=identity("contractId"),serviceId=identity("serviceId");
      const contract=generated?.contracts.find(value=>value.descriptor.contractId===contractId);
      if(!contract?.imported||contract.imported.authorityKind!=="package"||contract.exposure!=="public"||!serviceId||!Object.hasOwn(contract.descriptor.services,serviceId))return fail(targetNode,"Runtime target must be a canonical public package service in the validated imported materialization closure.","runtime-use-invalid");
      const key=contractId+"/"+serviceId;if(seen.has(key))return fail(item,"Declare each Runtime target only once on a provider.","runtime-use-invalid");seen.add(key);
      const applicable=applicableUseActions(contract),actions=policy.actions??applicable;
      if(policy.required!==undefined&&typeof policy.required!=="boolean"||!Array.isArray(actions)||!actions.length||new Set(actions).size!==actions.length||actions.some(action=>!applicable.includes(action as typeof applicable[number])))return fail(item,"Runtime actions must be a nonempty applicable subset and required must be Boolean.","runtime-use-invalid");
      const target={contractId:contract.descriptor.contractId,contractVersion:contract.descriptor.version,descriptorSha256:contract.descriptorSha256,abiSha256:contract.abiSha256,serviceId};
      uses.push({role:"runtime",...target,serviceType:canonicalServiceType(contract.imported.authorityId,serviceId),required:policy.required===true,actions:applicable.filter(action=>actions.includes(action))});
      metadata.push({service:target,...policy});
    }
    return {uses,metadata};
  };
  // Generated aliases are projections of reviewed identities, not new authored
  // declarations. Resolve both token namespaces and nested binding types back to
  // one representative symbol/name from the selected metadata.
  const importedSymbols = new Map<ts.Symbol, ts.Symbol>(); const importedNames = new Map<ts.Symbol, string>();
  const representatives = new Map<string, ts.Symbol>();
  for (const contract of generated?.contracts ?? []) {
    if (!contract.imported) continue;
    const source = program.getSourceFile(`${generated!.root}/imported/${contract.contractKey}.ts`);
    const module = source && checker.getSymbolAtLocation(source); if (!module) continue;
    const exports = new Map(checker.getExportsOfModule(module).map(symbol => [symbol.name,symbol]));
    const model = authoringBindingModel(contract.descriptor);
    for (const [key,name] of Object.entries(contract.modelProjection.models).sort(([a],[b])=>a<b?-1:a>b?1:0)) {
      const symbol = exports.get(model.definitionNames.get(key)!); if (!symbol) fail(source!,"Imported model symbol is absent from the validated binding.","evidence-mismatch");
      const identity=contract.imported.authorityKind+":"+contract.imported.authorityId+"/"+name;
      const representative=representatives.get(identity)??symbol;representatives.set(identity,representative);
      importedSymbols.set(symbol,representative);importedNames.set(representative,name);
    }
  }
  const modelSymbol = (symbol: ts.Symbol | undefined, seen = new Set<ts.Symbol>()): ts.Symbol | undefined => {
    if (!symbol || seen.has(symbol) || seen.size>=256) return undefined; seen.add(symbol);
    const imported = importedSymbols.get(symbol); if (imported) return imported;
    const declaration=symbol.declarations?.[0];
    if (!generated || !declaration?.getSourceFile().fileName.replaceAll("\\","/").startsWith(generated.root+"/")) return symbol;
    if (ts.isTypeAliasDeclaration(declaration)) {
      const node=declaration.type;
      if(ts.isTypeReferenceNode(node))return modelSymbol(symbolAt(node.typeName),seen);
      if(ts.isImportTypeNode(node)&&node.qualifier)return modelSymbol(symbolAt(node.qualifier),seen);
    }
    // Unmapped compiler envelopes and Unit never acquire source-model identity.
    return undefined;
  };
  function literal(node: ts.Expression, depth = 0): Data {
    budget(node, depth);
    if (ts.isParenthesizedExpression(node) || ts.isSatisfiesExpression(node) || ts.isAsExpression(node) && node.type.getText() === "const") return literal(node.expression, depth + 1);
    if (ts.isStringLiteralLike(node)) return node.text;
    if (ts.isNumericLiteral(node)) return Number(node.text);
    if (ts.isPrefixUnaryExpression(node) && node.operator === ts.SyntaxKind.MinusToken && ts.isNumericLiteral(node.operand)) return -Number(node.operand.text);
    if (node.kind === ts.SyntaxKind.TrueKeyword) return true;
    if (node.kind === ts.SyntaxKind.FalseKeyword) return false;
    if (node.kind === ts.SyntaxKind.NullKeyword) return null;
    if (ts.isArrayLiteralExpression(node)) return node.elements.map(item => literal(item, depth + 1));
    if (ts.isCallExpression(node) && symbolAt(node.expression) === errorDefinition) {
      if (node.arguments.length > 1) return fail(node, "defineServiceError accepts only description metadata.");
      const value = node.arguments[0] ? literal(node.arguments[0], depth + 1) : {};
      if (!value || typeof value !== "object" || Array.isArray(value)) return fail(node, "Error options require a description record.");
      const location = ts.isParenthesizedExpression(node.parent) && ts.getJSDocType(node.parent) ? node.parent : node;
      const type = checker.getTypeAtLocation(location); const details = checker.getTypeArguments(type as ts.TypeReference)[0];
      if (details && !(details.flags & ts.TypeFlags.Void)) errorTypes.set(value, { type: details, node });
      return value;
    }
    if (ts.isObjectLiteralExpression(node)) {
      const result: RecordData = Object.create(null) as RecordData; const keys = new Set<string>();
      for (const member of node.properties) {
        if (!ts.isPropertyAssignment(member) || !member.name || ts.isComputedPropertyName(member.name)) fail(member, "Semantic options require literal data properties; no spread/getter/computed members.");
        const key = member.name.getText().replace(/^['"]|['"]$/gu, "");
        if (keys.has(key.toLowerCase())) fail(member, "Semantic options contain a duplicate/case-colliding key."); keys.add(key.toLowerCase());
        result[key] = literal(member.initializer, depth + 1);
      }
      return result;
    }
    if (ts.isIdentifier(node)) {
      const symbol = symbolAt(node); const decl = symbol?.valueDeclaration;
      if (!symbol || !decl || !ts.isVariableDeclaration(decl) || !ts.isVariableDeclarationList(decl.parent) || !(decl.parent.flags & ts.NodeFlags.Const) || !decl.initializer || constants.has(symbol)) fail(node, "Semantic metadata references must be acyclic local constants.");
      resolvedConstants.add(symbol); if (resolvedConstants.size > 4096) fail(node, "Semantic constant budget exceeded.", "source-limit-exceeded");
      metadataReferences.add(node);
      constants.add(symbol); const value = literal(decl.initializer, depth + 1); constants.delete(symbol);
      if (value && typeof value === "object") metadataSymbols.add(symbol);
      return value;
    }
    return fail(node, "Semantic metadata must be bounded literal data; package code is never executed.");
  }
  const record = (value: Data | undefined, node: ts.Node): RecordData => {
    if (value === undefined) return {};
    if (!value || typeof value !== "object" || Array.isArray(value)) return fail(node, "Expected a closed semantic options record.");
    return value;
  };
  const only = (value: RecordData, names: readonly string[], node: ts.Node): void => { for (const key of Object.keys(value)) if (!names.includes(key)) fail(node, `Unknown semantic option '${key}'.`); };
  const module = checker.getSymbolAtLocation(file); if (!module) return fail(file, "Service module must be an ES module.");
  const declaredServices=checker.getExportsOfModule(module).map(symbol=>symbol.flags&ts.SymbolFlags.Alias?checker.getAliasedSymbol(symbol):symbol).filter(symbol=>{
    const node=symbol.valueDeclaration;return !!node&&ts.isVariableDeclaration(node)&&!!node.initializer&&ts.isCallExpression(node.initializer)&&symbolAt(node.initializer.expression)===define;
  });
  const serviceIdentities=new Map<ts.Symbol,RpcServiceReferenceProjection>();
  const declaredIdentity=(symbol:ts.Symbol,seen=new Set<ts.Symbol>()):RpcServiceReferenceProjection=>{
    const known=serviceIdentities.get(symbol);if(known)return known;if(seen.has(symbol))return fail(file,"Cyclic service identity.");seen.add(symbol);
    const declaration=symbol.valueDeclaration;
    if(!declaration||!ts.isVariableDeclaration(declaration)||!declaration.initializer||!ts.isCallExpression(declaration.initializer)||symbolAt(declaration.initializer.expression)!==define)return fail(declaration??file,"ServiceReference must identify a declared service, not a callback/object.");
    const options=declaration.initializer.arguments[1]?resolveMetadataExpression(declaration.initializer.arguments[1]!):undefined;let metadata:RecordData={};let implementation:ts.Expression|undefined;
    if(options&&ts.isObjectLiteralExpression(options)){
      const values:RecordData=Object.create(null) as RecordData;
      for(const property of options.properties){if(!ts.isPropertyAssignment(property)||ts.isComputedPropertyName(property.name))return fail(property,"Service identity requires literal semantic options.");
        const key=property.name.getText().replace(/^['"]|['"]$/gu,"");if(key==="implements")implementation=property.initializer;else if(key!=="runtimeUses")values[key]=literal(property.initializer);}
      metadata=values;
    }else if(options)metadata=record(literal(options),options);
    let result:RpcServiceReferenceProjection;
    if(implementation){
      const target=symbolAt(implementation);if(target&&declaredServices.includes(target))result=declaredIdentity(target,seen);
      else if(ts.isPropertyAccessExpression(implementation)&&imported.has(implementation.name.text)){
        const descriptor=imported.get(implementation.name.text)!.descriptor;const ids=Object.keys(descriptor.services);
        if(ids.length!==1)return fail(implementation,"A service reference must identify one descriptor service.");result={contractId:descriptor.contractId,serviceId:ids[0]!};
      }else return fail(implementation,"ServiceReference target is not a selected service declaration.");
    }else{const key=serviceKey(symbol.name);result={contractId:String(metadata.contractId??`${owner}.${key.replaceAll("-",".")}`),serviceId:String(metadata.serviceId??key)};}
    serviceIdentities.set(symbol,result);return result;
  };
  const definitions = new Map<ts.Symbol, SourceService>(); const pending = new Set<ts.Symbol>();
  const compile = (symbol: ts.Symbol): SourceService => {
    if (definitions.has(symbol)) return definitions.get(symbol)!;
    if (pending.has(symbol)) return fail(file, "Cyclic service implementation declaration."); pending.add(symbol);
    const decl = symbol.valueDeclaration;
    if (!decl || !ts.isVariableDeclaration(decl) || !decl.initializer || !ts.isCallExpression(decl.initializer) || symbolAt(decl.initializer.expression) !== define
      || !ts.isVariableDeclarationList(decl.parent) || !(decl.parent.flags & ts.NodeFlags.Const)) return fail(decl ?? file, "Services must be exported const defineService definitions.");
    const call = decl.initializer;
    if (call.arguments.length < 1 || call.arguments.length > 2) fail(call, "defineService takes methods and optional semantic options.");
    const metadataNode = call.arguments[1]?resolveMetadataExpression(call.arguments[1]!):undefined;
    let implementation: ts.Expression | undefined;
    let runtimeUsesNode:ts.Expression|undefined;
    let metadata: Data = {};
    if (metadataNode && ts.isObjectLiteralExpression(metadataNode)) {
      const members = metadataNode.properties.filter(member => {
        if (ts.isPropertyAssignment(member) && member.name.getText() === "implements") { implementation = member.initializer; return false; } return true;
      });
      // Walk original nodes: no synthetic source positions or package execution.
      const result: RecordData = Object.create(null) as RecordData;
      for (const member of members) {
        if (!ts.isPropertyAssignment(member) || ts.isComputedPropertyName(member.name)) fail(member, "Semantic options require literal data members.");
        const name = member.name.getText().replace(/^['"]|['"]$/gu, "");
        if (Object.keys(result).some(key => key.toLowerCase() === name.toLowerCase())) fail(member, "Duplicate semantic option.");
        if(name==="runtimeUses"){if(runtimeUsesNode)fail(member,"Duplicate runtimeUses declaration.","runtime-use-invalid");runtimeUsesNode=member.initializer;}
        else result[name] = literal(member.initializer);
      }
      metadata = result;
    } else if (metadataNode) metadata = literal(metadataNode);
    const options = record(metadata, call);
    only(options, ["exposure", "contractId", "serviceId", "providerId", "audience", "includeSchemaDialect", "appUse", "runtimeUse", "constraints", "errors", "methods", "declarationOnly"], call);
    if (options.exposure !== undefined && options.exposure !== "package" && options.exposure !== "public") fail(call, "Exposure must be package or public.");
    for (const flag of ["declarationOnly", "includeSchemaDialect"]) if (options[flag] !== undefined && typeof options[flag] !== "boolean") fail(call, `${flag} must be a boolean.`);
    let key = serviceKey(symbol.name); let inherited: SourceService | undefined; let importedContract: LoadedContract | undefined; let importKey: string | undefined;
    if (implementation) {
      const referenced = symbolAt(implementation);
      if (referenced?.valueDeclaration && ts.isVariableDeclaration(referenced.valueDeclaration) && referenced.valueDeclaration.getSourceFile() === file) inherited = compile(referenced);
      else if (ts.isPropertyAccessExpression(implementation) && ts.isIdentifier(implementation.expression)) {
        const base = symbolAt(implementation.expression);
        if (!base?.declarations?.some(node => node.getSourceFile().fileName.replaceAll("\\", "/").endsWith("/.sunder/generated/rpc/api.ts")) || base.name !== "Implementations") fail(implementation, "Imported service implementation must use the exact generated Implementations token.");
        importKey = implementation.name.text; importedContract = imported.get(importKey);
        if (!importedContract) fail(implementation, `No selected provider API '${importKey}'; run api implement/platform implement and restore.`);
      } else fail(implementation, "Implementation must reference a local declaration or a lock-generated service token.");
      if (["exposure", "contractId", "serviceId", "constraints", "errors", "appUse", "runtimeUse", "includeSchemaDialect"].some(name => options[name] !== undefined)) fail(call, "Implementations cannot redeclare their contract or acquire consumer policy.");
    }
    const methodsNode = call.arguments[0]!;
    const declarationOnly = options.declarationOnly === true;
    if(runtimeUsesNode&&(!importKey||declarationOnly))fail(runtimeUsesNode,"Typed runtimeUses belongs on a bound imported provider.","runtime-use-invalid");
    const typedUses=runtimeUsesNode?typedRuntimeUses(runtimeUsesNode):{uses:[],metadata:[]};
    let methodsType = checker.getTypeAtLocation(methodsNode);
    if (!declarationOnly) {
      let expression: ts.Expression = methodsNode; const references = new Set<ts.Symbol>();
      while (!ts.isObjectLiteralExpression(expression)) {
        if (ts.isParenthesizedExpression(expression) || ts.isSatisfiesExpression(expression)) { expression = expression.expression; continue; }
        if (!ts.isIdentifier(expression)) fail(expression, "Service methods must be a literal method record or a statically resolved const record.");
        const referenced = symbolAt(expression); const source = referenced?.valueDeclaration;
        if (!referenced || references.has(referenced) || !source || !ts.isVariableDeclaration(source) || !source.initializer || !ts.isVariableDeclarationList(source.parent) || !(source.parent.flags & ts.NodeFlags.Const)) fail(expression, "Service method records require acyclic const references.");
        references.add(referenced); expression = source.initializer;
      }
      for (const member of expression.properties) if (ts.isSpreadAssignment(member) || !member.name || ts.isComputedPropertyName(member.name) || ts.isGetAccessorDeclaration(member) || ts.isSetAccessorDeclaration(member)) fail(member, "Service method records cannot spread or compute their inventory.");
    }
    if (declarationOnly) {
      if (methodsNode.kind !== ts.SyntaxKind.NullKeyword) fail(call, "Declaration-only services use null and an ordinary interface or checked JSDoc ServiceDefinition type.");
      const declared = call.typeArguments?.[0];
      const annotation = ts.getJSDocType(decl);
      const definitionType = annotation ? checker.getTypeFromTypeNode(annotation) : checker.getTypeAtLocation(decl.name);
      const methodsProperty = checker.getPropertyOfType(definitionType, "methods");
      const propertyType = methodsProperty ? checker.getTypeOfSymbolAtLocation(methodsProperty, decl) : undefined;
      const inferred = propertyType?.isUnion() ? propertyType.types.find(type => !(type.flags & ts.TypeFlags.Null)) : propertyType;
      if (!declared && !inferred) fail(call, "Declaration-only services need an explicit ordinary interface or checked JSDoc ServiceDefinition type.");
      methodsType = declared ? checker.getTypeFromTypeNode(declared) : inferred!;
    }
    const exposure = inherited?.exposure ?? importedContract?.exposure ?? options.exposure ?? "package";
    const contractId = inherited?.descriptor.contractId ?? importedContract?.descriptor.contractId ?? options.contractId ?? `${owner}.${key.replaceAll("-", ".")}`;
    const serviceId = inherited?.descriptor.services && Object.keys(inherited.descriptor.services)[0] || options.serviceId || key;
    if (typeof contractId !== "string" || !isPackageId(contractId) || typeof serviceId !== "string") fail(call, "Service identity must be canonical bounded ASCII.", "service-invalid");
    if (inherited) key = inherited.key; else if (importedContract) key = importedContract.contractKey!;
    const accepted = baseline?.contracts.get(key) ?? [...baseline?.contracts.values() ?? []].find(entry => entry.contractId === contractId && hasPublicEvidence(entry));
    const contractVersion = exposure === "package" ? packageVersion : accepted?.current?.descriptor.version ?? "1.0.0";
    const defs: Record<string, Schema> = Object.create(null) as Record<string, Schema>; const named = new Map<ts.Symbol, string>(); const activeTypes = new Set<ts.Type>();
    const models: Record<string, string> = Object.create(null) as Record<string, string>; const sourceModels = new Map<string, string>();
    const serviceReferences:Record<string,RpcServiceReferenceProjection>=Object.create(null) as Record<string,RpcServiceReferenceProjection>;
    const optionalRecords=new Map<Schema,string[]>();
    const methodTimeouts:Record<string,{timeoutSeconds:number}>=Object.create(null) as Record<string,{timeoutSeconds:number}>;
    let naturalContent=false;let rawContent=false;
    const serviceType = `NonNullable<typeof import(${JSON.stringify(file.fileName.replace(/\.[jt]s$/u, ".js"))})[${JSON.stringify(symbol.name)}]["methods"]>`;
    const annotationFor = (node: ts.Node): ts.TypeNode | undefined => ts.isParameter(node) || ts.isPropertyDeclaration(node) || ts.isPropertySignature(node) || ts.isMethodDeclaration(node) || ts.isFunctionDeclaration(node)
      ? node.type ?? (ts.isMethodDeclaration(node) || ts.isFunctionDeclaration(node) ? ts.getJSDocReturnType(node) : ts.getJSDocType(node))
      : ts.isJSDocPropertyTag(node) ? node.typeExpression?.type : ts.isTypeNode(node) ? node : undefined;
    const optionalValue = (node: ts.Node): ts.Type | undefined => {
      const annotation = annotationFor(node); if (!annotation) return undefined;
      const type = checker.getTypeFromTypeNode(annotation);
      return (type.isUnion() ? type.types : [type]).some(item => item.flags & ts.TypeFlags.Undefined) ? undefined : type;
    };
    const sourceSymbol = (type: ts.Type, node: ts.Node): ts.Symbol | undefined => {
      let symbol = modelSymbol(type.aliasSymbol ?? type.symbol);
      // Primitive aliases are erased by the checker. Recover their declaration
      // identity from the effective TS/JSDoc annotation, never from schema text.
      const annotation = annotationFor(node);
      const unwrap = (typeNode: ts.TypeNode | undefined): ts.Symbol | undefined => {
        if (!typeNode) return undefined;
        if (ts.isTypeReferenceNode(typeNode)) {
          const value = symbolAt(typeNode.typeName);
          if (["Promise", "AsyncIterable", "AsyncGenerator", "AsyncIterableIterator", "ServiceStream"].includes(value?.name ?? "")) return unwrap(typeNode.typeArguments?.[0]);
          return value;
        }
        if (ts.isParenthesizedTypeNode(typeNode)) return unwrap(typeNode.type);
        if (ts.isImportTypeNode(typeNode) && typeNode.qualifier) return symbolAt(typeNode.qualifier);
        return undefined;
      };
      const annotated = unwrap(annotation);
      if (annotated?.declarations?.some(declaration => ts.isTypeAliasDeclaration(declaration) || ts.isJSDocTypedefTag(declaration)) && checker.getDeclaredTypeOfSymbol(annotated) === type) symbol = modelSymbol(annotated);
      if (!symbol || symbol.name.startsWith("__") || !symbol.declarations?.some(declaration =>
        (ts.isInterfaceDeclaration(declaration) || ts.isTypeAliasDeclaration(declaration) || ts.isEnumDeclaration(declaration) || ts.isJSDocTypedefTag(declaration))
        && !declaration.getSourceFile().fileName.replaceAll("\\", "/").startsWith(sdkRoot + "/")
        && !declaration.getSourceFile().fileName.replaceAll("\\", "/").includes("/typescript/lib/"))) return undefined;
      return symbol;
    };
    const facets = record(options.constraints, call); const usedFacets = new Set<string>();
    const sdkType = (type: ts.Type, name: string): boolean => {
      const seen = new Set<ts.Symbol>();
      const follows = (symbol: ts.Symbol | undefined): boolean => {
        if (!symbol || seen.has(symbol) || seen.size >= 64) return false; seen.add(symbol);
        if ((symbol.name === name || name==="ServiceReference"&&symbol.name==="ServiceReferenceValue") && symbol.declarations?.some(node => node.getSourceFile().fileName.replaceAll("\\", "/").startsWith(sdkRoot + "/"))) return true;
        return symbol.declarations?.some(declaration => {
          const target = ts.isTypeAliasDeclaration(declaration) ? declaration.type
            : ts.isJSDocTypedefTag(declaration) && declaration.typeExpression && ts.isJSDocTypeExpression(declaration.typeExpression) ? declaration.typeExpression.type : undefined;
          return target && (ts.isTypeReferenceNode(target) ? follows(symbolAt(target.typeName))
            : ts.isImportTypeNode(target) && target.qualifier ? follows(symbolAt(target.qualifier)) : false);
        }) === true;
      };
      return follows(type.aliasSymbol) || follows(type.symbol);
    };
    const referenceTarget=(type:ts.Type,node:ts.Node):RpcServiceReferenceProjection=>{
      if(type.isIntersection())type=type.types.find(item=>sdkType(item,"ServiceReference"))??type;
      const arguments_=checker.getTypeArguments(type as ts.TypeReference);
      const argument=type.aliasSymbol?.name==="ServiceReference"?type.aliasTypeArguments?.[0]??arguments_[1]:arguments_[1];if(!argument)return fail(node,"ServiceReference needs a declared service type.");
      const search=(annotation:ts.TypeNode|undefined,seen=new Set<ts.Symbol>()):ts.Symbol|undefined=>{
        if(!annotation)return undefined;
        if(ts.isTypeQueryNode(annotation))return symbolAt(annotation.exprName);
        if(ts.isTypeReferenceNode(annotation)||ts.isImportTypeNode(annotation)){
          for(const item of annotation.typeArguments??[]){const found=search(item,seen);if(found)return found;}
          const symbol=ts.isTypeReferenceNode(annotation)?symbolAt(annotation.typeName):annotation.qualifier?symbolAt(annotation.qualifier):undefined;
          if(symbol&&!seen.has(symbol)&&seen.size<64){seen.add(symbol);for(const declaration of symbol.declarations??[]){const rhs=ts.isTypeAliasDeclaration(declaration)?declaration.type:ts.isJSDocTypedefTag(declaration)&&declaration.typeExpression&&ts.isJSDocTypeExpression(declaration.typeExpression)?declaration.typeExpression.type:undefined;const found=search(rhs,seen);if(found)return found;}}
        }
        if(ts.isArrayTypeNode(annotation)||ts.isParenthesizedTypeNode(annotation))return search(ts.isArrayTypeNode(annotation)?annotation.elementType:annotation.type,seen);
        if(ts.isUnionTypeNode(annotation))for(const item of annotation.types){const found=search(item,seen);if(found)return found;}
        return undefined;
      };
      const authored=search(annotationFor(node));if(authored&&declaredServices.includes(authored))return declaredIdentity(authored);
      const token=(argument.isIntersection()?argument.types:[argument]).some(item=>sdkType(item,"ServiceToken"));
      if(token){
        if(authored&&!authored.declarations?.some(declaration=>generated&&declaration.getSourceFile().fileName.replaceAll("\\","/").startsWith(generated.root+"/")))
          return fail(node,"ServiceReference tokens must come from the exact generated Services export, not an authored factory/callback.","service-reference-invalid");
        const generatedIdentity=(argument.isIntersection()?argument.types:[argument]).some(part=>checker.getPropertyOfType(part,"serviceId")?.declarations?.some(declaration=>generated&&declaration.getSourceFile().fileName.replaceAll("\\","/").startsWith(generated.root+"/")));
        if(!generatedIdentity)return fail(node,"ServiceReference requires the generated token's literal service identity, not an authored token shape.","service-reference-invalid");
        const text=(name:string)=>{const property=checker.getPropertyOfType(argument,name);const value=property&&checker.getTypeOfSymbolAtLocation(property,node);return value?.isStringLiteral()?value.value:undefined;};
        const contractId=text("contractId"),serviceId=text("serviceId");
        if(contractId&&serviceId&&(generated?.contracts.some(contract=>contract.descriptor.contractId===contractId&&Object.hasOwn(contract.descriptor.services,serviceId))||declaredServices.some(symbol=>{const id=declaredIdentity(symbol);return id.contractId===contractId&&id.serviceId===serviceId;})))return {contractId,serviceId};
      }
      if(authored)return fail(node,"ServiceReference must name an exported registered/selected service declaration.","service-reference-invalid");
      const candidates=declaredServices.filter(symbol=>checker.getTypeAtLocation(symbol.valueDeclaration!)===argument);
      if(candidates.length===1&&sdkType(argument,"ServiceDefinition"))return declaredIdentity(candidates[0]!);
      return fail(node,"ServiceReference<T> must use typeof a declared source service or an exact generated Services token; callbacks and ambiguous structural types are unsupported.","service-reference-invalid");
    };
    const constrained = (schema: Schema, path: string): Schema => {
      if (!Object.hasOwn(facets, path)) return schema; usedFacets.add(path);
      const values = record(facets[path], call); const { format, ...rest } = values;
      const result = { ...schema, ...rest };
      if (format === "integer") { result.type = "integer"; delete result.format; }
      else if (format !== undefined) result.format = format;
      for (const name of ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"]) if (typeof result[name] === "string") { result[`value${name[0]!.toUpperCase()}${name.slice(1)}`] = result[name]; delete result[name]; }
      return result;
    };
    function union(types: readonly ts.Type[], node: ts.Node, path: string, depth: number, expression?: string): Schema {
      budget(node, depth);
      if (!types.length) return fail(node, `No portable present value type at ${path}.`, "value-invalid");
      if (types.length === 1) return schema(types[0]!, node, path, depth + 1, false, expression);
      if (types.some(type => type.flags & ts.TypeFlags.Null)) return { oneOf: [{ type: "null" }, union(types.filter(type => !(type.flags & ts.TypeFlags.Null)), node, path, depth + 1)] };
      if (types.every(type => type.flags & ts.TypeFlags.BooleanLiteral)) return constrained({ type: "boolean" }, path);
      if (types.every(type => type.flags & ts.TypeFlags.StringLiteral)) return constrained({ type: "string", enum: types.map(type => (type as ts.StringLiteralType).value) }, path);
      if (types.every(type => type.flags & ts.TypeFlags.NumberLiteral)) {
        const values = types.map(type => (type as ts.NumberLiteralType).value).sort((a, b) => a - b);
        if (values.every(Number.isSafeInteger)) return constrained({ type: "integer", enum: values, minimum: values[0], maximum: values.at(-1) }, path);
      }
      return constrained({ oneOf: types.map(type => schema(type, node, path, depth + 1)) }, path);
    }
    const optionalReference=(type:ts.Type):boolean=>checker.getPropertiesOfType(type).some(property=>property.declarations?.some(declaration=>ts.isPropertySignature(declaration)&&ts.isComputedPropertyName(declaration.name)&&ts.isIdentifier(declaration.name.expression)&&declaration.name.expression.text==="optionalServiceReferenceBrand"&&declaration.getSourceFile().fileName.replaceAll("\\","/").startsWith(sdkRoot+"/")));
    function schema(type: ts.Type, node: ts.Node, path: string, depth = 0, inline = false, expression?: string,optionalOccurrence=false): Schema {
      budget(node, depth);
      if (type.flags & (ts.TypeFlags.Any | ts.TypeFlags.Unknown | ts.TypeFlags.Never | ts.TypeFlags.Undefined | ts.TypeFlags.TypeParameter)) return fail(node, `Incomplete/nonportable type at ${path}; add a supported TS annotation or checked JSDoc.`, "value-invalid");
      const marked=optionalReference(type);
      if(marked&&!optionalOccurrence)fail(node,"OptionalServiceReference requires a direct nullable wire-required record property.","service-reference-invalid");
      if(sdkType(type,"ServiceReference")||marked){
        const target=referenceTarget(type,node);const name="ServiceReference_"+createHash("sha256").update(target.contractId+"\0"+target.serviceId).digest("hex").slice(0,32);
        if(Object.hasOwn(defs,name)&&!Object.hasOwn(serviceReferences,name))return fail(node,"Service reference definition collides with a source model.","identity-collision");
        serviceReferences[name]=target;defs[name]=serviceReferenceSchema();return {$ref:`#/$defs/${name}`};
      }
      if(sdkType(type,"SunderContent"))naturalContent=true;
      const identity = !inline ? sourceSymbol(type, node) : undefined;
      if (identity) {
        let projected = modelSymbols.get(identity);
        if (projected && projected.type !== type && identity.declarations?.some(declaration => (ts.isTypeAliasDeclaration(declaration) || ts.isInterfaceDeclaration(declaration)) && declaration.typeParameters?.length)) {
          const source = projected.use.getSourceFile(); const position = source.getLineAndCharacterOfPosition(projected.use.getStart(source));
          return fail(node, `Model '${projected.name}' has a different constructed source identity at ${source.fileName}:${position.line + 1}:${position.character + 1}; use concrete named aliases.`, "identity-collision");
        }
        const known = named.get(identity); if (known) return { $ref: `#/$defs/${known}` };
        if (!projected) {
          const name = importedNames.get(identity) ?? pascal(identity.name); const previous = modelNames.get(name.toLowerCase());
          if (previous && previous.symbol !== identity) {
            const location = previous.location.getSourceFile(); const position = location.getLineAndCharacterOfPosition(previous.location.getStart(location));
            return fail(identity.declarations?.[0] ?? node, `Model '${name}' conflicts with a distinct source declaration at ${location.fileName}:${position.line + 1}:${position.character + 1}.`, "identity-collision");
          }
          projected = { name, location: identity.declarations![0]!, type, use: node }; modelSymbols.set(identity, projected); modelNames.set(name.toLowerCase(), { symbol: identity, location: projected.location });
        }
        const definition = projected.name;
        if (Object.hasOwn(defs, definition)) return fail(node, `Definition '${definition}' collides with a generated value.`, "identity-collision");
        named.set(identity, definition); models[definition] = projected.name; defs[definition] = {};
        if (expression) sourceModels.set(projected.name, expression);
        else {
          const sourceFile = identity.declarations![0]!.getSourceFile(); const module = checker.getSymbolAtLocation(sourceFile);
          const exported = module && checker.getExportsOfModule(module).find(value => (value.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(value) : value) === identity);
          if (exported && /^[A-Za-z_$][A-Za-z0-9_$]*$/u.test(exported.name)) sourceModels.set(projected.name, `import(${JSON.stringify(sourceFile.fileName.replace(/\.[jt]s$/u, ".js"))}).${exported.name}`);
        }
        defs[definition] = schema(type, node, identity.name, depth + 1, true, expression);
        return { $ref: `#/$defs/${definition}` };
      }
      if(sdkType(type,"SunderContent"))return constrained({type:"object",logicalType:"content-reference"},path);
      if (sdkType(type, "RpcContentReference")) {rawContent=true;return constrained({ type: "object", logicalType: "content-reference" }, path);}
      if (sdkType(type, "JsonValue")) return constrained({}, path);
      if (sdkType(type, "JsonObject")) return constrained({ type: "object" }, path);
      if (type.flags & ts.TypeFlags.StringLiteral) return constrained({ type: "string", const: (type as ts.StringLiteralType).value }, path);
      if (type.flags & ts.TypeFlags.NumberLiteral) {
        const value = (type as ts.NumberLiteralType).value;
        return constrained({ type: Number.isSafeInteger(value) ? "integer" : "number", ...(!Number.isSafeInteger(value) ? { format: "float64" } : {}), const: value }, path);
      }
      if (type.flags & ts.TypeFlags.String) return constrained({ type: "string" }, path);
      if (type.flags & ts.TypeFlags.Number) return constrained({ type: "number", format: "float64" }, path);
      if (type.flags & ts.TypeFlags.BigIntLike) return constrained({ type: "string", format: "big-integer" }, path);
      if (type.flags & ts.TypeFlags.Boolean) return constrained({ type: "boolean" }, path);
      if (type.flags & ts.TypeFlags.BooleanLiteral) return constrained({ type: "boolean", const: checker.typeToString(type) === "true" }, path);
      if (type.flags & ts.TypeFlags.Null) return { type: "null" };
      if (type.isUnion()) {
        const annotation = annotationFor(node);
        if (!inline && !type.aliasSymbol && annotation && ts.isUnionTypeNode(annotation)) {
          const parts = annotation.types.map(node => ({ node, type: checker.getTypeFromTypeNode(node) })).filter(part => !(part.type.flags & ts.TypeFlags.Undefined));
          const values = parts.filter(part => !(part.type.flags & ts.TypeFlags.Null));
          if (parts.some(part => part.type.flags & ts.TypeFlags.Null) && values.length === 1 && sourceSymbol(values[0]!.type, values[0]!.node))
            return { oneOf: [{type:"null"}, schema(values[0]!.type, values[0]!.node, path, depth + 1, false, expression ? `NonNullable<${expression}>` : undefined)] };
        }
        return union(type.types, node, path, depth);
      }
      if (checker.isTupleType(type)) return fail(node, `Tuple ${path} has no V1 value representation; use an ordinary record/list.`, "value-invalid");
      if (type.symbol?.name === "Uint8Array" && type.symbol.declarations?.some(item => /\/typescript\/lib\/lib\./u.test(item.getSourceFile().fileName))) return constrained({ type: "string", contentEncoding: "base64" }, path);
      if (checker.isArrayType(type) || type.symbol?.name === "ReadonlyArray") {
        const item = checker.getTypeArguments(type as ts.TypeReference)[0]; if (!item) return fail(node, `Missing list element type at ${path}.`);
        return constrained({ type: "array", items: schema(item, node, `${path}[]`, depth + 1, false, expression ? `(${expression})[number]` : undefined) }, path);
      }
      if (!(type.flags & ts.TypeFlags.Object) || type.isClass() || checker.getSignaturesOfType(type, ts.SignatureKind.Call).length) return fail(node, `Unsupported value type at ${path}.`, "value-invalid");
      const properties = checker.getPropertiesOfType(type); const index = checker.getIndexTypeOfType(type, ts.IndexKind.String);
      if (index) {
        if (properties.length) return fail(node, `Maps cannot mix named properties at ${path}.`, "value-invalid");
        return constrained({ type: "object", additionalProperties: schema(index, node, `${path}[]`, depth + 1, false, expression ? `(${expression})[string]` : undefined), propertyNames: { type: "string" } }, path);
      }
      if (activeTypes.has(type)) return fail(node, `Anonymous recursive shape at ${path} requires a named source type.`, "value-invalid");
      activeTypes.add(type);
      const values: Record<string, Schema> = Object.create(null) as Record<string, Schema>; const required: string[] = [];const optionalMembers:string[]=[];
      for (const property of properties) {
        const declaration = property.valueDeclaration ?? property.declarations?.[0] ?? node;
        if (!/^[a-z][A-Za-z0-9]*$/u.test(property.name) || ts.isGetAccessorDeclaration(declaration) || ts.isSetAccessorDeclaration(declaration)) return fail(declaration, `Record ${path} requires ordinary camel-case data properties.`, "value-invalid");
        let value = checker.getTypeOfSymbolAtLocation(property, declaration); const optional = !!(property.flags & ts.SymbolFlags.Optional);
        const branches=value.isUnion()?value.types:[value];const markedBranch=branches.find(optionalReference);
        if(markedBranch){
          if(optional||branches.length!==2||!branches.some(item=>item.flags&ts.TypeFlags.Null))fail(declaration,"OptionalServiceReference requires a direct nullable wire-required record property.","service-reference-invalid");
          values[property.name]={oneOf:[{type:"null"},schema(markedBranch,declaration,`${path}.${property.name}`,depth+1,false,expression?`NonNullable<(${expression})[${JSON.stringify(property.name)}]>`:undefined,true)]};
          required.push(property.name);optionalMembers.push(property.name);continue;
        }
        if (optional && value.isUnion()) {
          const declared = optionalValue(declaration); const members = value.types.filter(item => !(item.flags & ts.TypeFlags.Undefined));
          if (declared) value = declared; else if (members.length === 1) value = members[0]!;
          else { values[property.name] = union(members, declaration, `${path}.${property.name}`, depth + 1); continue; }
        }
        values[property.name] = schema(value, declaration, `${path}.${property.name}`, depth + 1, false, expression ? `Exclude<(${expression})[${JSON.stringify(property.name)}], undefined>` : undefined); if (!optional) required.push(property.name);
      }
      activeTypes.delete(type);
      const result=constrained({ type: "object", properties: values, required, additionalProperties: false }, path);
      if(optionalMembers.length)optionalRecords.set(result,optionalMembers.sort());return result;
    }
    const ownMethods: ServiceMethod[] = []; const procedures: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    const methodOptions = record(options.methods, call);
    const existingMethods = inherited?.methods ?? (importedContract ? Object.entries(importedContract.descriptor.services).flatMap(([id, service]) => Object.entries(service.methods).map(([wireName, method]) => ({
      name: Object.keys(importedContract!.descriptor.services).length === 1 ? camel(wireName) : camel(id) + pascal(wireName), wireName, serviceId: id,
      parameters: [{ name: "request", optional: false }], control: "context" as const, stream: method.kind === "server-stream", unit: false, directRequest: true,
    }))) : undefined);
    const members = checker.getPropertiesOfType(methodsType);
    if (!members.length || members.length > 512 || checker.getIndexTypeOfType(methodsType, ts.IndexKind.String)) fail(methodsNode, "Service methods must be a nonempty bounded statically named record.");
    for (const member of members) {
      const node = member.valueDeclaration ?? member.declarations?.[0] ?? methodsNode;
      if (ts.isGetAccessorDeclaration(node) || ts.isSetAccessorDeclaration(node) || "name" in node && node.name && ts.isComputedPropertyName(node.name as ts.Node)) fail(node, "Service methods cannot be getters/computed members.");
      const signatures = checker.getSignaturesOfType(checker.getTypeOfSymbolAtLocation(member, node), ts.SignatureKind.Call);
      if (signatures.length !== 1 || signatures[0]!.typeParameters?.length) fail(node, `Service method '${member.name}' must have one nongeneric signature.`);
      const signature = signatures[0]!; const parameters: { name: string; optional: boolean }[] = []; const properties: Record<string, Schema> = {}; const required: string[] = [];
      let control: "signal" | "context" | undefined;
      for (const parameter of signature.parameters) {
        const source = parameter.valueDeclaration ?? parameter.declarations?.[0] ?? node;
        const type = checker.getTypeOfSymbolAtLocation(parameter, source);
        if (sdkType(type, "RpcInvocationContext") || sdkType(type, "ServiceContext")) { if (control) fail(source, "Only one invocation control parameter is allowed."); control = "context"; continue; }
        if (type.symbol?.name === "AbortSignal" && type.symbol.declarations?.some(item => /\/(?:typescript\/lib|@types\/node)\//u.test(item.getSourceFile().fileName))) { if (control) fail(source, "Only one invocation control parameter is allowed."); control = "signal"; continue; }
        if (control) fail(source, "Invocation control must be the final parameter.");
        if (ts.isParameter(source) && (source.dotDotDotToken || source.initializer || !ts.isIdentifier(source.name))) fail(source, "Rest/default/destructured service parameters need a named ordinary value parameter.");
        if (!/^[a-z][A-Za-z0-9]*$/u.test(parameter.name)) fail(source, "Service parameters must be camel-case names.");
        const optional = !!(parameter.flags & ts.SymbolFlags.Optional) || ts.isParameter(source) && !!source.questionToken;
        const present = optional ? optionalValue(source) : undefined;
        const value = present ? [present] : optional && type.isUnion() ? type.types.filter(item => !(item.flags & ts.TypeFlags.Undefined)) : [type];
        const expression = `Exclude<Parameters<${serviceType}[${JSON.stringify(member.name)}]>[${parameters.length}], undefined>`;
        properties[parameter.name] = existingMethods ? {} : value.length === 1 ? schema(value[0]!, source, `${member.name}.${parameter.name}`, 0, false, expression)
          : optional ? union(value, source, `${member.name}.${parameter.name}`, 0, expression) : schema(type, source, `${member.name}.${parameter.name}`, 0, false, expression);
        parameters.push({ name: parameter.name, optional }); if (!optional) required.push(parameter.name);
      }
      let output = checker.getReturnTypeOfSignature(signature); let stream = false;
      if (["AsyncIterable", "AsyncGenerator", "AsyncIterableIterator"].includes(output.symbol?.name ?? "") || sdkType(output,"ServiceStream")) { stream = true; output = checker.getTypeArguments(output as ts.TypeReference)[0] ?? output; }
      else output = checker.getAwaitedType(output) ?? output;
      const unit = !!(output.flags & (ts.TypeFlags.Void | ts.TypeFlags.Undefined));
      if (stream && unit) fail(node, "Stream events need a portable value type; void completion is unary only.", "value-invalid");
      const method = record(methodOptions[member.name], node); only(method, implementation ? ["completeOnResponseContentRetirement"] : ["name", "idempotency", "errors", "timeoutSeconds", "completeOnResponseContentRetirement"], node);
      if(method.completeOnResponseContentRetirement!==undefined&&(typeof method.completeOnResponseContentRetirement!=="boolean"||declarationOnly))fail(node,"completeOnResponseContentRetirement must be a literal boolean on a concrete provider method.","value-invalid");
      const wireName = method.name === undefined ? serviceKey(member.name) : String(method.name);
      if(method.timeoutSeconds!==undefined){if(typeof method.timeoutSeconds!=="number"||!Number.isInteger(method.timeoutSeconds)||method.timeoutSeconds<0||method.timeoutSeconds>2100)fail(node,"Method timeoutSeconds must be an integer in 0..2100.","value-invalid");
        if(method.timeoutSeconds>0)methodTimeouts[wireName]={timeoutSeconds:method.timeoutSeconds};}
      const requestName = `${pascal(key.replaceAll(".", "-"))}${pascal(member.name)}Request`; const resultName = unit ? "Unit" : `${pascal(key.replaceAll(".", "-"))}${pascal(member.name)}${stream ? "Event" : "Result"}`;
      const directRequest = parameters.length === 1 && !parameters[0]!.optional && typeof properties[parameters[0]!.name]?.$ref === "string";
      if (!existingMethods) {
        if ((!parameters.length || unit) && Object.hasOwn(models, "Unit")) fail(node, "Compiler Unit collides with an authored model.", "identity-collision");
        if (!directRequest && Object.hasOwn(defs, requestName) || !unit && Object.hasOwn(defs, resultName)) fail(node, "Synthetic service value name collides with an authored type.", "identity-collision");
        let requestSchema: Schema;
        if (directRequest) requestSchema = properties[parameters[0]!.name]!;
        else if (!parameters.length) { defs.Unit = { type: "object", properties: {}, required: [], additionalProperties: false }; requestSchema = { $ref: "#/$defs/Unit" }; }
        else { defs[requestName] = { type: "object", properties, required, additionalProperties: false }; requestSchema = { $ref: `#/$defs/${requestName}` }; }
        const returnType = `Awaited<ReturnType<${serviceType}[${JSON.stringify(member.name)}]>>`;
        const outputSchema = unit ? { type: "object", properties: {}, required: [], additionalProperties: false } : schema(output, node, `${member.name}.result`, 0, false,
          stream ? `(${returnType} extends AsyncIterable<infer T> ? T : never)` : returnType);
        let resultSchema: Schema;
        if (typeof outputSchema.$ref === "string") resultSchema = outputSchema;
        else { defs[resultName] = outputSchema; resultSchema = { $ref: `#/$defs/${resultName}` }; }
        if (Object.hasOwn(procedures, wireName)) fail(node, "Duplicate normalized method identity.", "identity-collision");
        procedures[wireName] = { kind: stream ? "server-stream" : "unary", idempotency: method.idempotency ?? "none", errors: method.errors ?? [], requestSchema, [stream ? "eventSchema" : "responseSchema"]: resultSchema };
      }
      ownMethods.push({ name: member.name, wireName, serviceId: String(serviceId), parameters, ...(control ? { control } : {}), stream, unit, ...(directRequest ? { directRequest } : {}), ...(method.completeOnResponseContentRetirement!==undefined?{completeOnResponseContentRetirement:method.completeOnResponseContentRetirement as boolean}:{}) });
    }
    if (Object.keys(methodOptions).some(name => !members.some(member => member.name === name))) fail(call, "Semantic method key does not identify a supported source member.");
    if (existingMethods && (members.length !== existingMethods.length || existingMethods.some(method => !members.some(member => member.name === method.name)))) fail(call, "Implementation must provide exactly every declared service method.", "handler-incomplete");
    const errors = record(options.errors, call); const errorDescriptors: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    for (const [code, value] of Object.entries(errors)) {
      const error = record(value, call); only(error, ["description"], call);
      const detail = errorTypes.get(error);
      let detailsSchema: Schema | undefined;
      if (detail) {
        const shape = schema(detail.type, detail.node, `errors.${code}`);
        if (typeof shape.$ref === "string") detailsSchema = shape;
        else { const name = `${pascal(code.replaceAll(".", "-"))}Details`; if (Object.hasOwn(defs, name)) fail(call, "Error detail name collision."); defs[name] = shape; detailsSchema = { $ref: `#/$defs/${name}` }; }
      }
      errorDescriptors[code] = { ...error, ...(detailsSchema ? { detailsSchema } : {}) };
    }
    if (Object.keys(facets).some(path => !usedFacets.has(path))) fail(call, "Semantic constraint key does not identify a supported source member.");
    const parseDescriptor = (): RpcContractDescriptor => { try { return parseRpcContractDescriptor(JSON.stringify({
      ...(options.includeSchemaDialect ? { $schema: "https://json-schema.org/draft/2020-12/schema" } : {}), descriptorVersion: 1, schemaProfile: "sunder.rpc.values.v1",
      contractId, version: contractVersion, errors: errorDescriptors, $defs: defs, services: { [String(serviceId)]: { methods: procedures } },
    })); } catch (error) { return fail(call, error instanceof Error ? error.message : String(error), "value-invalid"); } };
    const descriptor = inherited?.descriptor ?? importedContract?.descriptor ?? parseDescriptor();
    // Completion semantics belong to this implementation, never to inherited wire policy.
    const providerMethods=(existingMethods??ownMethods).map(method=>{
      const option=ownMethods.find(item=>item.name===method.name)?.completeOnResponseContentRetirement;
      const {completeOnResponseContentRetirement:_inherited,...shape}=method as ServiceMethod;
      if(option!==undefined){
        const wire=descriptor.services[method.serviceId]?.methods[method.wireName];
        const containsContent=(value:unknown,seen=new Set<string>()):boolean=>{
          if(!value||typeof value!=="object")return false;
          if(Array.isArray(value))return value.some(item=>containsContent(item,seen));
          const schema=value as Record<string,unknown>;
          if(schema.logicalType==="content-reference")return true;
          if(typeof schema.$ref==="string"){if(seen.has(schema.$ref))return false;seen.add(schema.$ref);return containsContent(descriptor.$defs[schema.$ref.slice("#/$defs/".length)],seen);}
          return Object.values(schema).some(item=>containsContent(item,seen));
        };
        if(wire?.kind!=="server-stream"||!(inherited?.naturalContent??importedContract?.naturalContent??naturalContent)||!containsContent(wire.eventSchema))fail(call,"completeOnResponseContentRetirement requires a server-stream response containing natural content.","value-invalid");
      }
      return {...shape,...(option!==undefined?{completeOnResponseContentRetirement:option}:{})};
    });
    const use = (name: "appUse" | "runtimeUse"): LoadedContract["appUse"] => {
      if (!Object.hasOwn(options, name)) return undefined;
      const value = record(options[name], call); only(value, ["required", "actions"], call);
      const applicable = ["discover", ...(ownMethods.some(method => !method.stream) ? ["invoke"] : []), ...(ownMethods.some(method => method.stream) ? ["subscribe"] : [])] as ("discover" | "invoke" | "subscribe")[];
      if (value.required !== undefined && typeof value.required !== "boolean") fail(call, `${name}.required must be boolean.`);
      const actions = value.actions ?? applicable;
      if (!Array.isArray(actions) || !actions.length || new Set(actions).size !== actions.length || actions.some(action => !applicable.includes(action as typeof applicable[number]))) fail(call, `${name}.actions must be a nonempty applicable set.`);
      return { required: value.required === true, actions: applicable.filter(action => actions.includes(action)) };
    };
    const providerId = declarationOnly ? undefined : String(options.providerId ?? `${contractId}.provider`);
    if (providerId && !isPackageId(providerId)) fail(call, "Provider identity must be bounded canonical ASCII.");
    const audience = options.audience ?? (exposure === "public" ? "public" : "owner");
    if (audience !== "public" && audience !== "owner" || audience === "public" && exposure !== "public") fail(call, "Provider audience is incompatible with service exposure.");
    // An ordinary owned concrete declaration intentionally supplies its own App
    // surface. Normalize that intent here, never from descriptor visibility,
    // consumer call sites, or the presence of an implementation/provider binding.
    const appUse = inherited?.appUse ?? use("appUse") ?? (!implementation && !declarationOnly && exposure === "public"
      ? { required: false, actions: applicableUseActions({ descriptor }) } : undefined);
    // A binding carries its declaration's policy even when exported before that
    // declaration; it cannot introduce or broaden either role's consumer intent.
    const runtimeUse = inherited?.runtimeUse ?? use("runtimeUse");
    if(naturalContent&&rawContent)fail(call,"A service contract cannot mix natural SunderContent and advanced raw RpcContentReference signatures; use SunderContent consistently.","value-invalid");
    const optionalServiceReferenceMembers=Object.fromEntries(Object.entries(defs).filter(([,schema])=>optionalRecords.has(schema)).map(([key,schema])=>[key,optionalRecords.get(schema)!]));
    if(Object.keys(optionalServiceReferenceMembers).length!==optionalRecords.size)fail(call,"OptionalServiceReference policy needs an admitted record definition, not an inline container or transparent value.","service-reference-invalid");
    const modelProjection = inherited?.modelProjection ?? importedContract?.modelProjection ?? readModelProjection(modelProjectionOuter({ models, serviceReferences,optionalServiceReferenceMembers }), descriptor);
    const methodPolicies=inherited?.methodPolicies??importedContract?.methodPolicies??readMethodPolicies(methodPoliciesOuter(Object.keys(methodTimeouts).length?{[String(serviceId)]:methodTimeouts}:undefined).methodPolicies,descriptor);
    const result: SourceService = { roleUses:typedUses.uses, methodPolicies, naturalContent:inherited?.naturalContent??importedContract?.naturalContent??naturalContent, modelProjection, sourceModels: inherited?.sourceModels ?? sourceModels, ownerDefinition: inherited?.ownerDefinition ?? importedContract?.descriptor.contractId ?? symbol.name, exportName: symbol.name, key, descriptor, exposure, methods: providerMethods,
      ...(providerId ? { providerId } : {}), audience, declarationOnly, semanticOptions: {...options,...(runtimeUsesNode?{runtimeUses:typedUses.metadata}:{})}, ...(importKey ? { importKey } : {}),
      ...(appUse ? { appUse } : {}), ...(runtimeUse ? { runtimeUse } : {}) };
    definitions.set(symbol, result); pending.delete(symbol); return result;
  };
  const services: SourceService[] = [];
  for (const exported of checker.getExportsOfModule(module)) {
    let symbol = exported;
    if (symbol.flags & ts.SymbolFlags.Alias) symbol = checker.getAliasedSymbol(symbol);
    const node = symbol.valueDeclaration;
    if (node && ts.isVariableDeclaration(node) && node.initializer && ts.isCallExpression(node.initializer) && symbolAt(node.initializer.expression) === define) services.push({ ...compile(symbol), exportName: exported.name });
  }
  if (services.length > 128) fail(file, "At most 128 service definitions are supported.", "source-limit-exceeded");
  const providerIds = new Set<string>(); const ids = new Map<string, string>();
  for (const service of services) {
    if (service.providerId && providerIds.has(service.providerId)) fail(file, `Multiple implementations require distinct explicit provider IDs (${service.providerId}).`, "identity-collision");
    if (service.providerId) providerIds.add(service.providerId);
    const previous = ids.get(service.key.toLowerCase()); if (previous && previous !== service.ownerDefinition) fail(file, "Service key collision; multiple implementations must bind the same declaration.", "identity-collision");
    ids.set(service.key.toLowerCase(), service.ownerDefinition);
  }
  const proveMetadata = (node: ts.Node): void => {
    if (ts.isTypeNode(node)) return;
    if (ts.isIdentifier(node) && !(ts.isVariableDeclaration(node.parent) && node.parent.name === node)) {
      const symbol = symbolAt(node);
      if (symbol && metadataSymbols.has(symbol) && !metadataReferences.has(node)) fail(node, "Semantic metadata constant mutation or escape outside its closed declaration is unsupported.");
    }
    ts.forEachChild(node, proveMetadata);
  };
  for (const source of program.getSourceFiles()) if (!source.isDeclarationFile) proveMetadata(source);
  return services;
}
