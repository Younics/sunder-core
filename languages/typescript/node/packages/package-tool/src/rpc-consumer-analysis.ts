import ts from "typescript";
import { authoringBindingModel } from "./generator";
import type { LoadedContract } from "./model";
import { consumerRequirements, requiredDefinitions, type ConsumerRequirements, type RequiredMethod } from "./rpc-consumer-requirements";

/** Ordinary typed method references only, never generated bodies, arbitrary
 * property-read inference, or a replacement for explicit raw/dynamic intent. */
export function analyzeConsumerRequirements(program: ts.Program, generatedRoot: string, contracts: readonly LoadedContract[], role: "runtime" | "app" = "runtime"): readonly ConsumerRequirements[] {
  const checker = program.getTypeChecker();
  const selected = contracts.filter(contract => contract.imported?.authorityKind === "package");
  const declarations = new Map<ts.Declaration, { contract: LoadedContract; method: RequiredMethod }>();
  const methods = new Map(selected.map(contract => [contract.descriptor.contractId, new Map<string, RequiredMethod>()]));
  const typed = new Map(selected.map(contract => [contract.descriptor.contractId, new Map<string, RequiredMethod>()]));
  const exact = new Map(selected.map(contract => [contract.descriptor.contractId, new Set(Object.keys(contract.modelProjection.serviceReferences))]));
  const views = new Map(selected.map(contract => [contract.descriptor.contractId, {} as Record<string, readonly string[]>]));
  const declaredViews: { id: string; name: string; members: readonly string[] }[] = [];
  let rawCalls = 0, dynamicDeclarations = 0;
  const add = (symbol: ts.Symbol | undefined, contract: LoadedContract, method: RequiredMethod): void => {
    for (const declaration of symbol?.declarations ?? []) if (declaration.getSourceFile().fileName.replaceAll("\\", "/").startsWith(generatedRoot + "/"))
      declarations.set(declaration, { contract, method });
  };
  // Service tokens retain literal identities. Their factory return types refer
  // to the exact generated method declarations used by injection/catalogs.
  const api = program.getSourceFile(generatedRoot + "/api.ts");
  if (api) {
    const visit = (node: ts.Node): void => {
      if (ts.isVariableDeclaration(node) && node.initializer && ts.isCallExpression(node.initializer)) {
        const type = checker.getTypeAtLocation(node.name);
        const identity = (name: string): string | undefined => {
          const member = checker.getPropertyOfType(type, name), value = member && checker.getTypeOfSymbolAtLocation(member, node.name);
          return value?.isStringLiteral() ? value.value : undefined;
        };
        const contract = selected.find(contract => contract.descriptor.contractId === identity("contractId")), serviceId = identity("serviceId");
        const factory = node.initializer.arguments[1];
        if (contract && serviceId && factory && Object.hasOwn(contract.descriptor.services, serviceId)) {
          const signature = checker.getTypeAtLocation(factory).getCallSignatures()[0];
          if (signature) {
            const result = checker.getReturnTypeOfSignature(signature);
            const service = authoringBindingModel(contract.descriptor).services.find(service => service.descriptor.serviceId === serviceId)!;
            for (const method of service.methods) {
              const name = method.methodName[0]!.toLowerCase() + method.methodName.slice(1);
              add(checker.getPropertyOfType(result, name), contract, { serviceId, methodId: method.descriptor.methodId });
            }
          }
        }
      }
      ts.forEachChild(node, visit);
    };
    visit(api);
  }
  for (const contract of selected) {
    const file = program.getSourceFile(generatedRoot + "/imported/" + contract.contractKey + ".ts");
    if (!file) continue;
    for (const service of authoringBindingModel(contract.descriptor).services) {
      const declaration = file.statements.find(statement => ts.isClassDeclaration(statement) && statement.name?.text === service.clientTypeName);
      if (!declaration || !ts.isClassDeclaration(declaration) || !declaration.name) continue;
      const type = checker.getTypeAtLocation(declaration.name);
      for (const method of service.methods) add(checker.getPropertyOfType(type, method.methodName), contract,
        { serviceId: service.descriptor.serviceId, methodId: method.descriptor.methodId });
    }
  }
  let visited = 0;
  const record = (symbol: ts.Symbol | undefined): void => {
    if (symbol?.flags && symbol.flags & ts.SymbolFlags.Alias) symbol = checker.getAliasedSymbol(symbol);
    for (const declaration of symbol?.declarations ?? []) {
      const entry = declarations.get(declaration);
      if (entry) {
        methods.get(entry.contract.descriptor.contractId)!.set(entry.method.serviceId + "\0" + entry.method.methodId, entry.method);
        typed.get(entry.contract.descriptor.contractId)!.set(entry.method.serviceId + "\0" + entry.method.methodId, entry.method);
      }
    }
  };
  for (const file of program.getSourceFiles()) {
    if (file.isDeclarationFile || file.fileName.replaceAll("\\", "/").startsWith(generatedRoot + "/") || program.isSourceFileFromExternalLibrary(file)) continue;
    const visit = (node: ts.Node): void => {
      if (++visited > 65_536) throw new Error("rpc-authoring.source-limit-exceeded: Typed consumer references exceed the source analysis budget.");
      if (ts.isCallExpression(node)) {
        const declaration = checker.getResolvedSignature(node)?.declaration;
        if (declaration && ts.isMethodSignature(declaration) && ts.isIdentifier(declaration.name)
          && ["invoke", "subscribe"].includes(declaration.name.text) && ts.isInterfaceDeclaration(declaration.parent)
          && ["RpcInvoker", "RpcClient", "RpcCallScope", "ApiTransport"].includes(declaration.parent.name.text)
          && /\/(?:core|types|api)\.d\.ts$/u.test(declaration.getSourceFile().fileName.replaceAll("\\", "/"))) rawCalls++;
        if (declaration && ts.isFunctionDeclaration(declaration) && declaration.name?.text === "declareRpcView"
          && declaration.getSourceFile().fileName.replaceAll("\\", "/").endsWith("/services.d.ts")) {
          const target = node.arguments[0], name = node.arguments[1], members = node.arguments[2], selectedRole = node.arguments[3];
          if (!target || !name || !ts.isStringLiteral(name) || !members || !ts.isArrayLiteralExpression(members)
            || members.elements.some(member => !ts.isStringLiteral(member)) || selectedRole && (!ts.isStringLiteral(selectedRole) || !["runtime", "app"].includes(selectedRole.text)))
            throw new Error("rpc-authoring.consumer-view-invalid: A raw view requires a canonical token, definition, literal member names and role.");
          if ((selectedRole && ts.isStringLiteral(selectedRole) ? selectedRole.text : "runtime") === role) {
            const type = checker.getTypeAtLocation(target), symbol = checker.getPropertyOfType(type, "contractId"), identity = symbol && checker.getTypeOfSymbolAtLocation(symbol, target);
            if (!identity?.isStringLiteral() || !methods.has(identity.value)) throw new Error("rpc-authoring.consumer-view-invalid: A raw view must name a selected package service.");
            declaredViews.push({ id: identity.value, name: name.text, members: members.elements.map(member => (member as ts.StringLiteral).text) });
          }
        }
        if (declaration && ts.isFunctionDeclaration(declaration) && declaration.name?.text === "declareRpcUse"
          && declaration.getSourceFile().fileName.replaceAll("\\", "/").endsWith("/services.d.ts")) {
          const target = node.arguments[0], ids = node.arguments[1], selectedRole = node.arguments[2];
          if (!target || !ids || !ts.isArrayLiteralExpression(ids) || selectedRole && (!ts.isStringLiteral(selectedRole) || !["runtime", "app"].includes(selectedRole.text)))
            throw new Error("rpc-authoring.consumer-requirements-invalid: declareRpcUse requires a canonical service token, literal method wire IDs and role.");
          if ((selectedRole && ts.isStringLiteral(selectedRole) ? selectedRole.text : "runtime") === role) {
            dynamicDeclarations++;
            const type = checker.getTypeAtLocation(target);
            const identity = (name: string): string | undefined => {
              const symbol = checker.getPropertyOfType(type, name), value = symbol && checker.getTypeOfSymbolAtLocation(symbol, target);
              return value?.isStringLiteral() ? value.value : undefined;
            };
            const contract = selected.find(contract => contract.descriptor.contractId === identity("contractId")), serviceId = identity("serviceId");
            if (!contract || !serviceId || ids.elements.length < 1 || ids.elements.length > 512)
              throw new Error("rpc-authoring.consumer-requirements-invalid: Dynamic use must name a selected imported package service.");
            for (const id of ids.elements) {
              if (!ts.isStringLiteral(id) || !Object.hasOwn(contract.descriptor.services[serviceId]?.methods ?? {}, id.text))
                throw new Error("rpc-authoring.consumer-requirements-invalid: Dynamic use must name existing method wire IDs.");
              methods.get(contract.descriptor.contractId)!.set(serviceId + "\0" + id.text, { serviceId, methodId: id.text });
            }
            for (const name of Object.keys(contract.descriptor.$defs)) exact.get(contract.descriptor.contractId)!.add(name);
          }
        }
      }
      if (ts.isIdentifier(node) || ts.isStringLiteralLike(node)) record(checker.getSymbolAtLocation(node));
      if (ts.isBindingElement(node) && ts.isObjectBindingPattern(node.parent) && ts.isVariableDeclaration(node.parent.parent)
        && node.parent.parent.initializer && !node.dotDotDotToken) {
        const name = node.propertyName ?? node.name;
        if (ts.isIdentifier(name) || ts.isStringLiteralLike(name)) record(checker.getPropertyOfType(checker.getTypeAtLocation(node.parent.parent.initializer), name.text));
      }
      ts.forEachChild(node, visit);
    };
    visit(file);
  }
  if (selected.length && rawCalls && !dynamicDeclarations)
    throw new Error("rpc-authoring.dynamic-requirements-required: Raw RPC with imported APIs requires declareRpcUse; typed clients collect requirements automatically.");
  for (const view of declaredViews) {
    const contract = selected.find(contract => contract.descriptor.contractId === view.id)!;
    if (!exact.get(view.id)!.has(view.name) || Object.hasOwn(contract.modelProjection.serviceReferences, view.name) || Object.hasOwn(views.get(view.id)!, view.name)
      || requiredDefinitions(consumerRequirements(contract.descriptor, [...typed.get(view.id)!.values()])).includes(view.name))
      throw new Error("rpc-authoring.consumer-view-invalid: A raw view must uniquely narrow a dynamically declared output, never a full typed model or protocol value.");
    exact.get(view.id)!.delete(view.name); views.get(view.id)![view.name] = view.members;
  }
  const result = new Map(selected.map(contract => [contract.descriptor.contractId,
    consumerRequirements(contract.descriptor, [...methods.get(contract.descriptor.contractId)!.values()], views.get(contract.descriptor.contractId)!, [...exact.get(contract.descriptor.contractId)!])]));
  const pending = [...result.keys()];
  for (let index = 0; index < pending.length; index++) {
    const id = pending[index]!, value = result.get(id)!, contract = selected.find(contract => contract.descriptor.contractId === id)!;
    for (const name of requiredDefinitions(value)) {
      const reference = contract.modelProjection.serviceReferences[name], target = reference && result.get(reference.contractId);
      if (!reference || !target) continue;
      if (Object.keys(target.outputMembers).length) throw new Error("rpc-authoring.consumer-view-invalid: A referenced typed service requires its full response models, not a raw view.");
      const selectedMethods = new Map(target.methods.map(method => [method.serviceId + "\0" + method.methodId, method]));
      for (const methodId of Object.keys(target.source.services[reference.serviceId]!.methods))
        selectedMethods.set(reference.serviceId + "\0" + methodId, { serviceId: reference.serviceId, methodId });
      if (selectedMethods.size === target.methods.length) continue;
      result.set(reference.contractId, consumerRequirements(target.source, [...selectedMethods.values()], target.outputMembers, target.exactOutputDefinitions));
      pending.push(reference.contractId);
    }
  }
  return Object.freeze([...result.values()].sort((a, b) => a.source.contractId < b.source.contractId ? -1 : a.source.contractId > b.source.contractId ? 1 : 0));
}
