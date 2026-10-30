using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    // Deliberately not whole-program/data-flow inference: collect ordinary typed
    // method references (including delegate captures) in authored trees only.
    // Generated client bodies must never turn the full import into requirements.
    private static IReadOnlyList<SunderRpcConsumerRequirements> CompileConsumerRequirements(
        CSharpCompilation compilation, SunderRpcCSharpCompilerInput input, IReadOnlySet<SyntaxTree> ordinaryTrees)
    {
        var contracts = CompleteImportedContracts(input).Where(contract => contract.AuthorityPackageId is not null).ToArray();
        var symbols = new Dictionary<ISymbol, (SunderRpcCSharpImportedContract Contract, SunderRpcRequiredMethod Method)>(SymbolEqualityComparer.Default);
        foreach (var contract in contracts)
        {
            var model = SunderRpcCSharpImportPrepass.GeneratorModel(contract);
            foreach (var service in model.Services)
            {
                var natural = contract.NaturalServices.Single(value => value.ServiceId == service.ServiceId);
                foreach (var name in new[] { natural.InterfaceMetadataName, natural.ClientMetadataName, contract.GeneratedLogicalName + "." + service.ClientTypeName })
                {
                    var type = compilation.GetTypeByMetadataName(name);
                    if (type is null || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                        || type.DeclaringSyntaxReferences.Length == 0 || type.DeclaringSyntaxReferences.Any(reference => ordinaryTrees.Contains(reference.SyntaxTree))) continue;
                    foreach (var method in service.Methods)
                        foreach (var symbol in type.GetMembers(method.MethodName).OfType<IMethodSymbol>())
                            symbols.Add(symbol.OriginalDefinition, (contract, new(service.ServiceId, method.MethodId)));
                }
            }
        }
        var methods = contracts.ToDictionary(contract => contract.ContractKey, _ => new HashSet<SunderRpcRequiredMethod>(), StringComparer.Ordinal);
        var typed = contracts.ToDictionary(contract => contract.ContractKey, _ => new HashSet<SunderRpcRequiredMethod>(), StringComparer.Ordinal);
        var exact = contracts.ToDictionary(contract => contract.ContractKey,
            contract => contract.ModelProjection.ServiceReferences.Keys.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var dynamicUse = ResolveAttribute<Sunder.Sdk.Services.SunderRpcDynamicUseAttribute>(compilation);
        var declarations = compilation.Assembly.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, dynamicUse)).ToArray();
        if (declarations.Length > 128) throw FailureAtLocations("rpc-authoring.source-limit-exceeded", "Dynamic RPC declarations exceed 128 entries.", []);
        foreach (var declaration in declarations)
        {
            var arguments = declaration.ConstructorArguments;
            var contract = contracts.SingleOrDefault(contract => arguments.Length == 3 && contract.Descriptor.ContractId == (arguments[0].Value as string));
            var serviceId = arguments.Length == 3 ? arguments[1].Value as string : null;
            if (contract is null || serviceId is null || arguments[2].IsNull || arguments[2].Values.Length is < 1 or > 512)
                throw FailureAtLocations("rpc-authoring.consumer-requirements-invalid", "Dynamic RPC declarations require a selected package contract, service and 1..512 existing method wire IDs.", []);
            foreach (var method in arguments[2].Values)
            {
                if (method.Value is not string id || contract.Descriptor.FindService(serviceId)?.FindMethod(id) is null)
                    throw FailureAtLocations("rpc-authoring.consumer-requirements-invalid", "Dynamic RPC declarations must name existing method wire IDs.", []);
                methods[contract.ContractKey].Add(new(serviceId, id));
            }
            exact[contract.ContractKey].UnionWith(contract.Descriptor.Definitions.Keys);
        }
        var names = symbols.Keys.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
        var references = 0;
        foreach (var tree in ordinaryTrees)
        {
            var semantic = compilation.GetSemanticModel(tree);
            if (contracts.Length != 0 && declarations.Length == 0)
                foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                    if (semantic.GetSymbolInfo(invocation).Symbol is IMethodSymbol raw && raw.Name is "InvokeAsync" or "SubscribeAsync"
                        && raw.ContainingType.ToDisplayString() is "Sunder.Sdk.Rpc.ISunderRpcClient" or "Sunder.Sdk.Rpc.ISunderRpcCallScope")
                        throw FailureAtLocations("rpc-authoring.dynamic-requirements-required", "Raw RPC with imported APIs requires assembly SunderRpcDynamicUse declarations; typed generated clients collect requirements automatically.", [invocation.GetLocation()]);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>().Where(name => names.Contains(name.Identifier.ValueText)))
            {
                if (++references > 65_536) throw FailureAtLocations("rpc-authoring.source-limit-exceeded", "Typed consumer method references exceed their analysis budget.", [name.GetLocation()]);
                var binding = semantic.GetSymbolInfo(name);
                IEnumerable<ISymbol> selected = binding.Symbol is { } symbol ? [symbol] : binding.CandidateSymbols;
                foreach (var candidate in selected.OfType<IMethodSymbol>())
                    if (symbols.TryGetValue(candidate.OriginalDefinition, out var method))
                    { methods[method.Contract.ContractKey].Add(method.Method); typed[method.Contract.ContractKey].Add(method.Method); }
            }
        }
        var views = contracts.ToDictionary(contract => contract.ContractKey, _ => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), StringComparer.Ordinal);
        var viewAttribute = ResolveAttribute<Sunder.Sdk.Services.SunderRpcConsumerViewAttribute>(compilation);
        foreach (var declaration in compilation.Assembly.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, viewAttribute)))
        {
            var arguments = declaration.ConstructorArguments;
            var contract = contracts.SingleOrDefault(contract => arguments.Length == 3 && contract.Descriptor.ContractId == (arguments[0].Value as string));
            var name = arguments.Length == 3 ? arguments[1].Value as string : null;
            if (contract is null || name is null || arguments[2].IsNull || !exact[contract.ContractKey].Contains(name)
                || contract.ModelProjection.ServiceReferences.ContainsKey(name)
                || new SunderRpcConsumerRequirements(contract.Descriptor, typed[contract.ContractKey].ToArray()).RequiredDefinitions.Contains(name, StringComparer.Ordinal)
                || !views[contract.ContractKey].TryAdd(name, arguments[2].Values.Select(value => value.Value as string ?? "").ToArray()))
                throw FailureAtLocations("rpc-authoring.consumer-view-invalid", "A raw consumer view must uniquely narrow a dynamically declared output, never a full typed model or service-reference protocol.", []);
            exact[contract.ContractKey].Remove(name);
        }
        try
        {
            return SunderRpcConsumerCompatibility.CloseServiceReferences(contracts.Select(contract => new SunderRpcConsumerRequirements(contract.Descriptor,
                methods[contract.ContractKey].ToArray(), views[contract.ContractKey], exact[contract.ContractKey].ToArray())).ToArray(),
                contracts.ToDictionary(contract => contract.Descriptor.ContractId, contract => contract.ModelProjection, StringComparer.Ordinal));
        }
        catch (InvalidDataException error) { throw FailureAtLocations("rpc-authoring.consumer-view-invalid", error.Message, []); }
    }
}
