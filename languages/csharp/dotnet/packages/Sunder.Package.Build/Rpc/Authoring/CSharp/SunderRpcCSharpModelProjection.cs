using Microsoft.CodeAnalysis;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpSourceModel(string Name, INamedTypeSymbol SourceType);

internal sealed record SunderRpcCSharpModelProjectionPlan(
    IReadOnlyList<SunderRpcCSharpCompiledContract> Contracts,
    IReadOnlyList<SunderRpcCSharpSourceModel> Models,
    IReadOnlyList<SunderRpcCSharpCompilerDiagnostic> Diagnostics)
{
    internal bool Success => Diagnostics.Count == 0;
}

// This table is computed once over the complete source compilation. Role/RID
// projection and declaration order cannot invent or merge source-model identity.
internal static class SunderRpcCSharpModelProjection
{
    internal static SunderRpcCSharpModelProjectionPlan Plan(IReadOnlyList<SunderRpcCSharpCompiledContract> contracts,
        IReadOnlyList<SunderRpcCSharpImportedContract>? imports = null)
    {
        var models = new Dictionary<INamedTypeSymbol, SunderRpcCSharpSourceModel>(SymbolEqualityComparer.Default);
        var names = new Dictionary<string, SunderRpcCSharpSourceModel>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<SunderRpcCSharpCompilerDiagnostic>();
        foreach (var value in contracts.SelectMany(contract => contract.SourceTypes)
                     .DistinctBy(value => value.SourceType, SymbolEqualityComparer.Default).OrderBy(value => value.SourceType.ToDisplayString(), StringComparer.Ordinal))
        {
            var source = value.SourceType;
            var publicName = value.PublicName;
            if (publicName is null && SunderRpcCSharpNaming.TryCreate(source.Name, null, out var name)) publicName = name!.Pascal;
            if (!SunderRpcModelProjection.IsPortableModelName(publicName))
            {
                diagnostics.Add(Error("rpc-authoring.model-projection-invalid", $"Source model '{source}' must have a public model name of 1–128 ASCII identifier characters, excluding reserved C#/TypeScript keywords. SunderValue.PublicName is used exactly as authored.", [source]));
                continue;
            }
            var model = new SunderRpcCSharpSourceModel(publicName!, source);
            if (names.TryGetValue(model.Name, out var previous))
            {
                diagnostics.Add(Error("rpc-authoring.generated-name-collision",
                    $"Distinct source models '{previous.SourceType}' and '{source}' collide at public model '{model.Name}' under ASCII case folding. Use distinct source model names or SunderValue.PublicName overrides; equal schemas do not establish identity.",
                    [previous.SourceType, source]));
                continue;
            }
            models.Add(source, model);
            names.Add(model.Name, model);
        }
        if (diagnostics.Count != 0) return new([], [], diagnostics);

        var projected = new List<SunderRpcCSharpCompiledContract>();
        foreach (var contract in contracts.OrderBy(value => value.ContractKey, StringComparer.Ordinal))
        {
            var reachable = ReachableDefinitions(contract.Descriptor);
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var optionalMembers = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (definition, members) in contract.ImportedOptionalServiceReferenceMembers)
                if (reachable.Contains(definition)) optionalMembers.Add(definition, members);
            foreach (var source in contract.SourceTypes)
            {
                if (!SunderRpcDescriptorParser.TryGetReferenceName(source.DefinitionReference, out var definition))
                    throw new InvalidOperationException("A validated source model has no descriptor definition reference.");
                // Retained historical definitions are not exported as new public
                // model names unless reachable from this contract's value API.
                if (reachable.Contains(definition)) map.Add(definition, models[source.SourceType].Name);
                var members = source.Properties.Where(property => SunderRpcCSharpSourceCompiler.IsOptionalReference(property.Source))
                    .Select(property => property.WireName).Order(StringComparer.Ordinal).ToArray();
                if (reachable.Contains(definition) && members.Length != 0) optionalMembers.Add(definition, members);
            }
            projected.Add(contract with { ModelProjection = new(map, contract.SourceServiceReferences.Where(pair => reachable.Contains(pair.Key)).ToDictionary(), optionalMembers) });
        }
        try
        {
            var imported = imports ?? [];
            SunderRpcModelProjectionGraph.Validate(projected.Select(Entry).Concat(imported.Select(contract => new SunderRpcModelProjectionContract(
                    contract.Descriptor.ContractId, contract.Descriptor.Version, contract.Descriptor.Sha256, contract.Descriptor.AbiSha256, contract.ModelProjection, contract.MethodPolicies))).ToArray(),
                projected.Select(contract => contract.Descriptor).Concat(imported.Select(contract => contract.Descriptor)).ToDictionary(descriptor => descriptor.ContractId, StringComparer.Ordinal),
                projected.Select(contract => (contract.Descriptor.ContractId, Authority: "package:" + contract.PackageId))
                    .Concat(imported.Select(contract => (contract.Descriptor.ContractId, Authority: "package:" + contract.AuthorityPackageId)))
                    .ToDictionary(pair => pair.ContractId, pair => pair.Authority, StringComparer.Ordinal));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("rpc-authoring.model-projection-invalid", exception.Message,
                contracts.Select(contract => contract.ServiceType).ToArray()));
        }
        var targets = projected.Select(contract => new KeyValuePair<string, SunderRpcCSharpResourceTarget>(contract.Descriptor.ContractId + "/" + contract.Descriptor.Services.Single().ServiceId,
            new(contract.Descriptor, contract.Descriptor.Services.Single().ServiceId, SunderRpcCSharpServiceApiEmitter.ApiType(contract), SunderRpcCSharpServiceApiEmitter.CatalogType(contract), contract.GeneratedLogicalName)))
            .Concat((imports ?? []).SelectMany(contract => contract.ResourceTargets)).GroupBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        return new(projected.Select(contract => contract with { ResourceTargets = targets }).ToArray(), models.Values.OrderBy(model => model.Name, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    internal static SunderRpcModelProjectionContract Entry(SunderRpcCSharpCompiledContract contract)
        => new(contract.Descriptor.ContractId, contract.Descriptor.Version, contract.Descriptor.Sha256,
            contract.Descriptor.AbiSha256, contract.ModelProjection, contract.MethodPolicies);

    internal static IReadOnlySet<string> ReachableDefinitions(SunderRpcContractDescriptor descriptor)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(descriptor.Services.SelectMany(service => service.Methods)
            .SelectMany(method => new[] { method.RequestSchemaReference, method.OutputSchemaReference })
            .Concat(descriptor.Errors.Values.Select(error => error.DetailsSchema).OfType<string>()));
        while (pending.TryDequeue(out var reference))
        {
            if (!SunderRpcDescriptorParser.TryGetReferenceName(reference, out var key)) throw new InvalidDataException("Invalid descriptor reference in source projection.");
            if (visited.Add(key)) Visit(descriptor.Definitions[key]);
        }
        return visited;

        void Visit(System.Text.Json.JsonElement value)
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (value.TryGetProperty("$ref", out var reference)) pending.Enqueue(reference.GetString()!);
                else foreach (var property in value.EnumerateObject()) Visit(property.Value);
            }
            else if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }

    private static SunderRpcCSharpCompilerDiagnostic Error(string id, string message, IReadOnlyList<INamedTypeSymbol> types)
    {
        var spans = types.SelectMany(type => type.Locations).Where(location => location.IsInSource).Select(location =>
        {
            var span = location.GetLineSpan();
            return new SunderRpcCSharpSourceSpan(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
        }).Distinct().OrderBy(span => span.Path, StringComparer.Ordinal).ThenBy(span => span.StartLine).ThenBy(span => span.StartColumn).ToArray();
        var first = spans.FirstOrDefault();
        return new(id, message, first?.Path, first?.StartLine, first?.StartColumn, first?.EndLine, first?.EndColumn,
            spans.Length > 1 ? spans.Skip(1).ToArray() : null);
    }
}
