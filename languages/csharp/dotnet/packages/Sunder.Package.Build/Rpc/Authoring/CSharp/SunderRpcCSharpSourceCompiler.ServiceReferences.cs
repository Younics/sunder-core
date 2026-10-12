using Microsoft.CodeAnalysis;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    private static IReadOnlyDictionary<string, SunderRpcServiceReferenceProjection> BuildServiceReferenceTargets(string packageId,
        IReadOnlyList<INamedTypeSymbol> services, AttributeSymbols attributes,
        IReadOnlyList<SunderRpcCSharpImportedProviderSurface> providers, IReadOnlyList<SunderRpcCSharpPackageApiSurface> imports)
    {
        var result = new Dictionary<string, SunderRpcServiceReferenceProjection>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            var declaration = GetAttribute(service, attributes.Service)!;
            var key = GetNamedString(declaration, "Key");
            if (!SunderRpcCSharpNaming.TryCreate(key ?? service.Name, key is null ? "Service" : null, out var name)) continue;
            var id = GetNamedString(declaration, "ContractId") ?? packageId + "." + name!.ContractSuffix;
            var serviceId = GetNamedString(declaration, "ServiceId") ?? name!.LowerKebab;
            var metadataName = Editor.GeneratedPackageNames.ServiceInterface(packageId, serviceId);
            result.TryAdd(metadataName, new(id, serviceId));
        }
        foreach (var contract in providers.Select(provider => provider.Contract).DistinctBy(contract => contract.ContractKey))
            foreach (var service in contract.NaturalServices)
                result.TryAdd(service.InterfaceMetadataName, new(contract.Descriptor.ContractId, service.ServiceId));
        foreach (var import in imports)
            foreach (var service in import.Services)
                result.TryAdd(service.InterfaceMetadataName, new(service.ContractId, service.ServiceId));
        return result;
    }
}
