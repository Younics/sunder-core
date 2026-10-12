using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpImportedUse(SunderRpcCSharpImportedContract Contract,
    SunderRpcCSharpNaturalService Service, SunderRpcOwnedUsePolicy Policy)
{
    internal SunderRpcRoleUse Evidence => new("runtime", Contract.Descriptor.ContractId, Contract.Descriptor.Version,
        Contract.Descriptor.Sha256, Contract.Descriptor.AbiSha256, Service.ServiceId, Service.InterfaceMetadataName, Policy);
}

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    private static IReadOnlyList<SunderRpcCSharpImportedUse> CompileImportedUses(CSharpCompilation compilation,
        SunderRpcCSharpCompilerInput input, IReadOnlySet<SyntaxTree> ordinaryTrees, IReadOnlyList<INamedTypeSymbol> sourceTypes,
        AttributeSymbols attributes, IReadOnlyList<SunderRpcCSharpImportedProvider> providers)
    {
        var selected = CompleteImportedContracts(input).Where(contract => contract.AuthorityPackageId is not null).ToArray();
        var result = new Dictionary<string, SunderRpcCSharpImportedUse>(StringComparer.Ordinal);
        foreach (var source in sourceTypes)
        {
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var attribute in source.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.RuntimeUse)
                         && attribute.ConstructorArguments.Length != 0))
            {
                if (!providers.Any(provider => SymbolEqualityComparer.Default.Equals(provider.Provider.SourceType, source)))
                    throw Failure("rpc-authoring.runtime-use-invalid", "Typed Runtime use belongs on a bound imported provider class.", source);
                if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol { TypeKind: TypeKind.Interface, IsGenericType: false } type)
                    throw Failure("rpc-authoring.runtime-use-invalid", "Typed Runtime use requires a canonical selected imported service interface.", source);
                if (!seen.Add(type)) throw Failure("rpc-authoring.runtime-use-invalid", "Declare each typed Runtime target only once on a provider class.", source);
                var matches = selected.SelectMany(contract => contract.NaturalServices.Select(service => (Contract: contract, Service: service)))
                    .Where(value => SymbolEqualityComparer.Default.Equals(type, compilation.GetTypeByMetadataName(value.Service.InterfaceMetadataName))).ToArray();
                if (matches.Length != 1 || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                    || type.DeclaringSyntaxReferences.Length != 1 || type.DeclaringSyntaxReferences.Any(reference => ordinaryTrees.Contains(reference.SyntaxTree)))
                    throw Failure("rpc-authoring.runtime-use-invalid", "The Runtime target must be the exact canonical generated service in the validated imported materialization closure.", source);
                var (contract, service) = matches[0];
                var expected = CSharpSyntaxTree.ParseText(SunderRpcCSharpImportPrepass.EmitNaturalServices(contract, consumers: false))
                    .GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>().Single(declaration => declaration.Identifier.ValueText == type.Name);
                if (type.DeclaringSyntaxReferences[0].GetSyntax().ToString() != expected.ToString())
                    throw Failure("rpc-authoring.runtime-use-invalid", "The Runtime target service declaration differs from its validated generated source.", source);
                var policy = CompileUsePolicy(source, contract.Descriptor, attribute, "runtime");
                if (result.Values.Any(previous => previous.Contract.ContractKey == contract.ContractKey
                        && !SunderRpcAuthoringDocuments.SameUsePolicy(previous.Policy, policy)))
                    throw Failure("rpc-authoring.runtime-use-invalid", "Conflicting typed Runtime intents select the same contract.", source);
                var key = contract.ContractKey + "/" + service.ServiceId;
                if (result.TryGetValue(key, out var previous))
                {
                    if (previous.Service != service || !SunderRpcAuthoringDocuments.SameUsePolicy(previous.Policy, policy))
                        throw Failure("rpc-authoring.runtime-use-invalid", "Conflicting typed Runtime intents select the same contract.", source);
                    continue;
                }
                result.Add(key, new(contract, service, policy));
                if (result.Count > MaximumContracts) throw Failure("rpc-authoring.source-limit-exceeded", "Typed Runtime uses exceed the contract limit.", source);
            }
        }
        return result.Values.OrderBy(use => use.Contract.ContractKey, StringComparer.Ordinal).ThenBy(use => use.Service.ServiceId, StringComparer.Ordinal).ToArray();
    }
}
