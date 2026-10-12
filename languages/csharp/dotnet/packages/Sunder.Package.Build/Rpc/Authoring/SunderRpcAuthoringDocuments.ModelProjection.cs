using System.Text;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcAuthoringDocuments
{
    internal const string ModelProjectionFileName = SunderPackageFormat.RpcModelProjectionFileName;
    internal const string ModelProjectionLogicalPath = SunderPackageFormat.RpcModelProjectionLogicalPath;
    internal const string ModelProjectionArchivePath = SunderPackageFormat.RpcModelProjectionPath;

    internal static bool SameModelProjection(SunderRpcModelProjection left, SunderRpcModelProjection right)
        => left.ToCanonicalUtf8().AsSpan().SequenceEqual(right.ToCanonicalUtf8());

    internal static bool SameProjectionContract(SunderRpcModelProjectionContract left, SunderRpcModelProjectionContract right)
        => left.ToCanonicalUtf8().AsSpan().SequenceEqual(right.ToCanonicalUtf8());

    private static void ModelProjectionProperty(StringBuilder output, SunderRpcModelProjection projection, bool comma)
    {
        var lines = Utf8NoBom.GetString(WriteDeterministicDocument(JsonSerializer.SerializeToElement(projection.ToJson())))
            .TrimEnd('\n').Split('\n');
        Line(output, 3, "\"modelProjection\": " + lines[0]);
        for (var index = 1; index < lines.Length; index++)
            Line(output, 3, lines[index] + (index == lines.Length - 1 && comma ? "," : string.Empty));
    }

    internal static byte[] PublicModelProjectionBytes(SunderRpcAuthoringReceiptProjection receipt)
        => PublicModelProjectionBytes(receipt.Manifest, receipt.Contracts, receipt.ModelProjections, receipt.MethodPolicies);

    internal static byte[] PublicModelProjectionBytes(SunderPackageManifest manifest,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> contracts,
        IReadOnlyDictionary<string, SunderRpcModelProjection> projections,
        IReadOnlyDictionary<string, SunderRpcMethodPolicies> methodPolicies)
    {
        var byId = contracts.ToDictionary(pair => pair.Value.ContractId, pair => pair.Key, StringComparer.Ordinal);
        var entries = (manifest.ContractBundles ?? []).Where(bundle => bundle!.Exposure == "public")
            .OrderBy(bundle => bundle!.ContractId, StringComparer.Ordinal).ThenBy(bundle => bundle!.Version, StringComparer.Ordinal)
            .Select(bundle => new SunderRpcModelProjectionContract(bundle!.ContractId!, bundle.Version!, bundle.DescriptorSha256!,
                bundle.AbiSha256!, projections[byId[bundle.ContractId!]], methodPolicies[byId[bundle.ContractId!]])).ToArray();
        var document = new SunderRpcModelProjectionDocument(1, manifest.Id!, manifest.Version!, entries);
        document.Validate(manifest, contracts.Values.ToDictionary(value => value.ContractId, StringComparer.Ordinal));
        return document.ToCanonicalUtf8();
    }

    private static void ValidateReceiptModelProjections(SunderPackageManifest manifest,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> contracts,
        IReadOnlyDictionary<string, SunderRpcModelProjection> projections)
    {
        ValidateModelProjectionGraph(manifest, contracts, projections);
        foreach (var role in (manifest.Targets ?? []).Select(target => target!.Role).Distinct(StringComparer.Ordinal))
        {
            var uses = (manifest.UsesContracts ?? []).Where(use => use!.Role == role).ToDictionary(use => use!.ContractId!, use => use!, StringComparer.Ordinal);
            var provides = (manifest.Provides ?? []).Where(provider => provider!.Role == role).Select(provider => provider!.ContractId!).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, descriptor) in contracts)
            {
                uses.TryGetValue(descriptor.ContractId, out var use);
                var methods = descriptor.Services.SelectMany(service => service.Methods).Where(method => provides.Contains(descriptor.ContractId)
                    || method.Kind == SunderRpcMethodKind.Unary && use?.Actions?.Contains("invoke", StringComparer.Ordinal) == true
                    || method.Kind == SunderRpcMethodKind.ServerStream && use?.Actions?.Contains("subscribe", StringComparer.Ordinal) == true).ToArray();
                // Complete inventory is still validated above. Only execution
                // permitted/provided in this role makes a value graph reachable;
                // discover-only use never admits a method's request/result values.
                foreach (var reference in ReachableServiceReferences(descriptor, projections[key], methods))
                    if (!uses.TryGetValue(reference.ContractId, out var targetUse)
                        || targetUse.Actions?.Contains("discover", StringComparer.Ordinal) != true)
                        throw MissingServiceReferenceUse(role!, reference.ContractId, descriptor.ContractId);
            }
        }
    }

    // Selection must reach the same role denial before committing a new sidecar
    // pin, rather than accepting the lock and failing later during receipt emit.
    // These contracts have already passed the complete pinned projection proof.
    internal static void ValidateImportedProviderReferenceUses(SunderRpcContractLock contractLock, SunderRpcCSharpCompilationResult compilation)
    {
        var discover = contractLock.PackageImports.Values.Where(import => import.ConsumerRoles.Contains("runtime", StringComparer.Ordinal))
            .SelectMany(import => contractLock.PackageArtifacts[import.PackageId].Contracts.Values.Where(contract => contract.AuthorityKind == "package"))
            .Select(contract => contract.ContractId).ToHashSet(StringComparer.Ordinal);
        foreach (var use in compilation.ImportedUses)
        {
            var consuming = contractLock.PackageImports.Values.Where(import => import.ConsumerRoles.Contains("runtime", StringComparer.Ordinal)
                && contractLock.PackageArtifacts[import.PackageId].Contracts.ContainsKey(use.Contract.ContractKey)).ToArray();
            if (consuming.Length != 0 && (use.Policy.Required != consuming.Any(import => import.RequiredContracts.Contains(use.Contract.ContractKey, StringComparer.Ordinal))
                    || !use.Policy.Actions.SequenceEqual(ReceiptActions(use.Contract.Descriptor), StringComparer.Ordinal)))
                throw new InvalidDataException("rpc-authoring.runtime-use-invalid: Typed use conflicts with the existing package API consumer policy; review one consistent intent.");
            if (use.Policy.Actions.Contains("discover", StringComparer.Ordinal)) discover.Add(use.Contract.ContractKey);
        }
        foreach (var contract in compilation.ImportedProviders.Select(provider => provider.Surface.Contract).DistinctBy(contract => contract.ContractKey))
            foreach (var reference in ReachableServiceReferences(contract.Descriptor, contract.ModelProjection,
                          contract.Descriptor.Services.SelectMany(service => service.Methods).ToArray()))
                if (!discover.Contains(reference.ContractId))
                    throw MissingServiceReferenceUse("runtime", reference.ContractId, contract.Descriptor.ContractId);
        foreach (var use in compilation.ImportedUses)
            foreach (var reference in ReachableServiceReferences(use.Contract.Descriptor, use.Contract.ModelProjection,
                         use.Contract.Descriptor.Services.SelectMany(service => service.Methods).Where(method =>
                             method.Kind == SunderRpcMethodKind.Unary && use.Policy.Actions.Contains("invoke", StringComparer.Ordinal)
                             || method.Kind == SunderRpcMethodKind.ServerStream && use.Policy.Actions.Contains("subscribe", StringComparer.Ordinal)).ToArray()))
                if (!discover.Contains(reference.ContractId))
                    throw MissingServiceReferenceUse("runtime", reference.ContractId, use.Contract.ContractKey);
    }

    private static InvalidDataException MissingServiceReferenceUse(string role, string targetId, string sourceId)
        => new($"rpc-authoring.service-reference-use-missing: Role '{role}' has no reviewed discover use of '{targetId}' referenced by reachable contract '{sourceId}'; a projected service reference cannot widen grants.");

    private static IEnumerable<SunderRpcServiceReferenceProjection> ReachableServiceReferences(SunderRpcContractDescriptor descriptor,
        SunderRpcModelProjection projection, IReadOnlyList<SunderRpcMethodDescriptor> methods)
    {
        var pending = new Queue<string>(methods.SelectMany(method => new[] { method.RequestSchemaReference, method.OutputSchemaReference })
            .Concat(methods.SelectMany(method => method.Errors).Distinct(StringComparer.Ordinal)
                .Select(code => descriptor.Errors[code].DetailsSchema).OfType<string>()));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var reference))
        {
            if (!SunderRpcDescriptorParser.TryGetReferenceName(reference, out var definition))
                throw new InvalidDataException("rpc-authoring.model-projection-invalid: Invalid reference in a validated service value graph.");
            if (!visited.Add(definition)) continue;
            if (projection.ServiceReferences.TryGetValue(definition, out var target)) yield return target;
            Visit(descriptor.Definitions[definition]);
        }

        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
                    pending.Enqueue(reference.GetString()!);
                else foreach (var member in value.EnumerateObject()) Visit(member.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }

    private static void ValidateModelProjectionGraph(SunderPackageManifest manifest,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> contracts,
        IReadOnlyDictionary<string, SunderRpcModelProjection> projections)
    {
        // Private projections remain compiler evidence and do not consume a public
        // sidecar byte allowance. Graph validation covers exact descriptor linkage.
        var entries = contracts.Select(pair => new SunderRpcModelProjectionContract(pair.Value.ContractId, pair.Value.Version,
            pair.Value.Sha256, pair.Value.AbiSha256, projections[pair.Key])).ToArray();
        SunderRpcModelProjectionGraph.Validate(entries, contracts.Values.ToDictionary(value => value.ContractId, StringComparer.Ordinal),
            (manifest.ContractBundles ?? []).ToDictionary(bundle => bundle!.ContractId!, bundle => bundle!.AuthorityKind + ":" + bundle.AuthorityId, StringComparer.Ordinal));
    }

    internal static SunderRpcAuthoringReceiptProjection MergeReceiptProjections(IReadOnlyList<SunderRpcAuthoringReceiptProjection> receipts)
    {
        var contracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        var projections = new Dictionary<string, SunderRpcModelProjection>(StringComparer.Ordinal);
        var policies = new Dictionary<string, SunderRpcMethodPolicies>(StringComparer.Ordinal);
        foreach (var receipt in receipts)
            foreach (var (key, descriptor) in receipt.Contracts)
            {
                var projection = receipt.ModelProjections[key];
                var methodPolicies = receipt.MethodPolicies[key];
                if (contracts.TryGetValue(key, out var previous)
                    && (previous.Sha256 != descriptor.Sha256 || previous.AbiSha256 != descriptor.AbiSha256
                        || !SameModelProjection(projections[key], projection) || !SameMethodPolicies(policies[key], methodPolicies)))
                    throw new InvalidDataException($"rpc-authoring.evidence-mismatch: Role receipts disagree on contract/model projection '{key}'.");
                contracts[key] = descriptor;
                projections[key] = projection;
                policies[key] = methodPolicies;
            }
        var manifest = Sunder.Package.Build.Tasks.AggregateSunderPackageTask.MergeAuthoringProjections(receipts.Select(receipt => receipt.Manifest).ToArray());
        ValidateModelProjectionGraph(manifest, contracts, projections);
        return new(manifest, contracts, projections, policies) { RoleUses = receipts.SelectMany(receipt => receipt.RoleUses).ToArray() };
    }
}
