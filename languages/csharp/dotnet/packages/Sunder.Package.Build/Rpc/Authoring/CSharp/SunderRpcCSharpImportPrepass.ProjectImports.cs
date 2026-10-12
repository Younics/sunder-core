using Sunder.Sdk.Packaging;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImportPrepass
{
    // ProjectImport has no authored model symbols. This is the complete source
    // handoff: transport/schema, canonical models, services/catalogs and facades.
    internal static IReadOnlyList<SunderRpcCSharpSourceFile> PrepareOwnedPackageApi(SunderRpcPackageProjectExport export, string packageVersion)
    {
        if (export.PackageVersion != packageVersion)
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Project import package version differs from its export.");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contracts = new Dictionary<string, SunderRpcCSharpImportedContract>(StringComparer.Ordinal);
        foreach (var entry in export.Contracts.OrderBy(entry => entry.Descriptor.ContractId, StringComparer.Ordinal))
        {
            if (entry.GeneratedLogicalName != Editor.GeneratedPackageNames.Transport(export.PackageId, entry.Descriptor.ContractId))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Project export transport namespace does not match its authority and contract.");
            if (entry.ModelProjection is null)
                throw new InvalidDataException("rpc-authoring.model-projection-invalid: Project export model projection is required.");
            var contract = PrepareNaturalContract(new(entry.Descriptor.ContractId, entry.GeneratedLogicalName, entry.Descriptor)
                { ModelProjection = entry.ModelProjection, MethodPolicies = entry.MethodPolicies, OwningPackageVersion = packageVersion }, export.PackageId, names);
            if (!contracts.TryAdd(entry.Descriptor.ContractId, contract))
                throw new InvalidDataException("rpc-authoring.identity-collision: Project export repeats a contract identity.");
        }
        var publicContracts = export.Contracts.Where(entry => entry.Exposure == SunderServiceExposure.Public)
            .Select(entry => entry.Descriptor.ContractId).ToHashSet(StringComparer.Ordinal);
        var sources = new List<SunderRpcCSharpSourceFile>(ProjectNaturalContracts(contracts, publicContracts).Sources);
        foreach (var contract in contracts.Values.OrderBy(contract => contract.ContractKey, StringComparer.Ordinal))
            sources.Add(new("natural-services/" + contract.ContractKey + ".g.cs", EmitNaturalServices(contract, consumers: true,
                publicContracts.Contains(contract.ContractKey) ? "public" : "internal")));
        if (contracts.Count != 0)
        {
            sources.Add(new("injected-apis/" + export.PackageId + ".lifetime.g.cs", SunderRpcCSharpServiceApiEmitter.EmitImportedLifetime(export.PackageId)));
            var range = PackageVersionRange.Parse("=" + packageVersion).ToString();
            EmitInjectedPackageApi(export.PackageId, Literal(export.PackageId) + ", " + Literal(range) + ", true",
                contracts.Values.OrderBy(contract => contract.ContractKey, StringComparer.Ordinal).ToArray(), sources, [],
                publicContracts.Count == contracts.Count ? "public" : "internal");
        }
        sources.AddRange(EmitOwnedPackageFacade(export.PackageId, export.Contracts.Select(entry => (entry.ContractKey,
            Contract: contracts[entry.Descriptor.ContractId])).ToArray(), packageVersion));
        return sources.OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
    }

    private static SunderRpcCSharpProjectedBindingSet ProjectNaturalContracts(Dictionary<string, SunderRpcCSharpImportedContract> contracts,
        IReadOnlySet<string>? publicContracts = null)
    {
        var projected = SunderRpcCSharpProjectedBindings.Generate(contracts.Values.Where(contract => contract.AuthorityPackageId is not null)
            .Select(contract => new SunderRpcCSharpProjectionInput(contract.AuthorityPackageId!, contract.GeneratedLogicalName, contract.Descriptor,
                contract.ModelProjection, publicContracts is null || publicContracts.Contains(contract.Descriptor.ContractId)) { MethodPolicies = contract.MethodPolicies }).ToArray());
        foreach (var (id, binding) in projected.Contracts)
            contracts[id] = contracts[id] with { ProjectedModel = binding.Model, TypeBindings = binding.Types };
        var targets = contracts.Values.SelectMany(contract => contract.NaturalServices.Select(service => new KeyValuePair<string, SunderRpcCSharpResourceTarget>(
            contract.Descriptor.ContractId + "/" + service.ServiceId, new(contract.Descriptor, service.ServiceId, "global::" + service.InterfaceMetadataName,
                "global::" + service.CatalogMetadataName, contract.GeneratedLogicalName)))).ToDictionary();
        foreach (var id in contracts.Keys.ToArray()) contracts[id] = contracts[id] with { ResourceTargets = targets };
        var complete = Array.AsReadOnly(contracts.Values.OrderBy(contract => contract.Descriptor.ContractId, StringComparer.Ordinal).ToArray());
        foreach (var id in contracts.Keys.ToArray()) contracts[id] = contracts[id] with { ProjectionSet = complete };
        return projected;
    }
}
