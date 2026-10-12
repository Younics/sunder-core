using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpImportedModel(INamedTypeSymbol Type, SunderRpcCSharpImportedContract Contract, string Definition);

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    private static IReadOnlyDictionary<INamedTypeSymbol, SunderRpcCSharpImportedModel> TrustedImportedModels(
        CSharpCompilation compilation, SunderRpcCSharpCompilerInput input, IReadOnlySet<SyntaxTree> sourceTrees)
    {
        var contracts = CompleteImportedContracts(input).Where(contract => contract.AuthorityPackageId is not null).ToArray();
        var result = new Dictionary<INamedTypeSymbol, SunderRpcCSharpImportedModel>(SymbolEqualityComparer.Default);
        if (contracts.Length == 0) return result;
        var expected = SunderRpcCSharpProjectedBindings.Generate(contracts.Select(contract => new SunderRpcCSharpProjectionInput(
            contract.AuthorityPackageId!, contract.GeneratedLogicalName, contract.Descriptor, contract.ModelProjection) { MethodPolicies = contract.MethodPolicies }).ToArray());
        var verifiedTrees = new HashSet<SyntaxTree>();
        foreach (var source in expected.Sources.Where(source => source.Path.StartsWith("models/", StringComparison.Ordinal)))
        {
            // The strict publication pass rebases logical sources into its owned
            // output tree. Path rebasing does not replace the exact byte/symbol
            // proof, and an authored tree can never become a trusted model.
            var trees = compilation.SyntaxTrees.Where(tree => tree.FilePath == source.Path
                || Path.IsPathFullyQualified(tree.FilePath) && tree.FilePath.Replace('\\', '/').EndsWith("/" + source.Path, StringComparison.Ordinal)).ToArray();
            if (trees.Length != 1 || sourceTrees.Contains(trees[0]) || trees[0].GetText().ToString() != source.Text)
                throw new InvalidDataException($"rpc-authoring.imported-model-invalid: Imported model source '{source.Path}' differs from the validated projection-generated source.");
            verifiedTrees.Add(trees[0]);
        }
        foreach (var contract in contracts)
        {
            var binding = expected.Contracts[contract.Descriptor.ContractId];
            var verified = contract with { ProjectedModel = binding.Model, TypeBindings = binding.Types };
            foreach (var (definition, name) in contract.ModelProjection.Models)
            {
                var metadataName = Editor.GeneratedPackageNames.Models(contract.AuthorityPackageId!) + "." + name;
                var type = compilation.GetTypeByMetadataName(metadataName);
                if (type is null || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                    || type.DeclaringSyntaxReferences.Length == 0
                    || type.DeclaringSyntaxReferences.Any(reference => !verifiedTrees.Contains(reference.SyntaxTree)))
                    throw new InvalidDataException($"rpc-authoring.imported-model-invalid: '{metadataName}' must be the exact validated generated source symbol, not authored or referenced-assembly metadata.");
                result.TryAdd(type, new(type, verified, definition));
            }
        }
        return result;
    }

    private static IReadOnlyList<SunderRpcCSharpImportedContract> CompleteImportedContracts(SunderRpcCSharpCompilerInput input)
    {
        var roots = (input.ImportedPackageApis ?? []).SelectMany(api => api.Contracts)
            .Concat((input.ImportedProviderSurfaces ?? []).Select(surface => surface.Contract)).ToArray();
        foreach (var root in roots.Where(contract => contract.ProjectionSet.Count != 0))
        {
            var exact = root.ProjectionSet.SingleOrDefault(contract => contract.Descriptor.ContractId == root.Descriptor.ContractId);
            if (exact is null || exact.AuthorityPackageId != root.AuthorityPackageId || exact.GeneratedLogicalName != root.GeneratedLogicalName
                || exact.Descriptor.Sha256 != root.Descriptor.Sha256 || exact.Descriptor.AbiSha256 != root.Descriptor.AbiSha256
                || !exact.ModelProjection.ToCanonicalUtf8().AsSpan().SequenceEqual(root.ModelProjection.ToCanonicalUtf8())
                || !exact.MethodPolicies.ToCanonicalUtf8().AsSpan().SequenceEqual(root.MethodPolicies.ToCanonicalUtf8()))
                throw new InvalidDataException("rpc-authoring.imported-model-invalid: Selected contract differs from its complete validated model-projection set.");
        }
        return roots.SelectMany(contract => contract.ProjectionSet.Count == 0 ? new[] { contract } : contract.ProjectionSet)
            .DistinctBy(contract => contract.Descriptor.ContractId).OrderBy(contract => contract.Descriptor.ContractId, StringComparer.Ordinal).ToArray();
    }

    private sealed partial class DefinitionGraph
    {
        private readonly Dictionary<string, SunderRpcCSharpResourceLayout> _importedLayouts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SunderRpcCSharpMarshallingPlan> _importedPlans = new(StringComparer.Ordinal);
        internal IReadOnlyDictionary<string, SunderRpcCSharpResourceLayout> ImportedLayouts => _importedLayouts;
        private readonly Dictionary<string, IReadOnlyList<string>> _importedOptionalServiceReferenceMembers = new(StringComparer.Ordinal);
        internal IReadOnlyDictionary<string, IReadOnlyList<string>> ImportedOptionalServiceReferenceMembers => _importedOptionalServiceReferenceMembers;

        private string ImportModel(SunderRpcCSharpImportedModel model, ISymbol source)
        {
            if (exposure != SunderServiceExposure.Package)
                throw Failure("rpc-authoring.imported-model-ownership", $"Public service values cannot claim imported model '{model.Type}' as owned. This compiler supports borrowed imported models in package-private services only; public cross-authority model identity requires coordinated metadata.", source);
            return ImportDefinition(model.Contract, model.Definition, source);
        }

        private string ImportDefinition(SunderRpcCSharpImportedContract contract, string definition, ISymbol source)
        {
            var projectedModel = contract.ModelProjection.Models.GetValueOrDefault(definition);
            if (projectedModel is not null)
            {
                var canonical = importedModels.Values.First(model => model.Contract.AuthorityPackageId == contract.AuthorityPackageId
                    && model.Contract.ModelProjection.Models[model.Definition] == projectedModel);
                contract = canonical.Contract;
                definition = canonical.Definition;
            }
            var identity = contract.AuthorityPackageId + "\0" + (projectedModel is null ? contract.Descriptor.ContractId + "\0" + definition : "model\0" + projectedModel);
            var key = "Imported_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            if (_definitions.ContainsKey(key)) return key;
            ReserveDefinition(key, source);
            _definitions.Add(key, new JsonObject()); // Reserve before walking recursive closures.
            var schema = JsonNode.Parse(contract.Descriptor.Definitions[definition].GetRawText())!;
            Rewrite(schema, contract, source);
            _definitions[key] = schema.AsObject();
            if (contract.ModelProjection.OptionalServiceReferenceMembers.TryGetValue(definition, out var optionalMembers))
                _importedOptionalServiceReferenceMembers.Add(key, optionalMembers);
            if (contract.ModelProjection.ServiceReferences.TryGetValue(definition, out var service)) _serviceReferences.Add(key, service);
            if (!_importedPlans.TryGetValue(contract.Descriptor.ContractId, out var plan))
            {
                plan = SunderRpcCSharpMarshallingPlan.Imported(contract);
                _importedPlans.Add(contract.Descriptor.ContractId, plan);
            }
            if (plan.Layouts.TryGetValue(definition, out var layout))
            {
                _importedLayouts[key] = RebindLayout(layout, contract, source);
                ImportInlineLayouts(layout, plan, contract, source);
            }
            return key;
        }

        private void ImportInlineLayouts(SunderRpcCSharpResourceLayout layout, SunderRpcCSharpMarshallingPlan plan,
            SunderRpcCSharpImportedContract contract, ISymbol source)
        {
            var types = layout.Members.Select(member => member.TypeName).Concat(new[] { layout.TransparentTypeName }.OfType<string>())
                .Concat(layout.Variants?.Values ?? []).SelectMany(type => SyntaxFactory.ParseTypeName(type).DescendantNodesAndSelf().OfType<TypeSyntax>())
                .Select(type => type.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (var (inlineKey, inline) in plan.Layouts.Where(pair => pair.Key.StartsWith('\0') && types.Contains(pair.Value.TypeName)))
            {
                var unique = "\0" + contract.Descriptor.ContractId + "/" + inlineKey;
                if (!_importedLayouts.TryAdd(unique, inline)) continue;
                _importedLayouts[unique] = RebindLayout(inline, contract, source);
                ImportInlineLayouts(inline, plan, contract, source);
            }
        }

        private SunderRpcCSharpResourceLayout RebindLayout(SunderRpcCSharpResourceLayout layout, SunderRpcCSharpImportedContract contract, ISymbol source)
            => layout with { Variants = layout.Variants?.ToDictionary(pair =>
            {
                if (!pair.Key.StartsWith('{')) return ImportDefinition(contract, pair.Key, source);
                var schema = JsonNode.Parse(pair.Key)!;
                Rewrite(schema, contract, source);
                return schema.ToJsonString();
            }, pair => pair.Value, StringComparer.Ordinal) };

        private void Rewrite(JsonNode value, SunderRpcCSharpImportedContract contract, ISymbol source)
        {
            if (value is JsonObject record)
            {
                if (record["$ref"] is JsonValue reference)
                    record["$ref"] = "#/$defs/" + ImportDefinition(contract, reference.GetValue<string>()["#/$defs/".Length..], source);
                else foreach (var child in record.ToArray()) if (child.Value is not null) Rewrite(child.Value, contract, source);
            }
            else if (value is JsonArray array)
                foreach (var child in array) if (child is not null) Rewrite(child, contract, source);
        }
    }
}
