using System.Text;
using Microsoft.CodeAnalysis;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpResourceTarget(SunderRpcContractDescriptor Descriptor, string ServiceId,
    string InterfaceName, string CatalogName, string SchemaNamespace);

internal sealed record SunderRpcCSharpMarshallingPlan(SunderRpcContractDescriptor Descriptor, SunderRpcModelProjection Projection,
    IReadOnlyDictionary<string, SunderRpcCSharpResourceLayout> Layouts,
    IReadOnlyDictionary<string, SunderRpcCSharpResourceTarget> Targets, string SchemaNamespace)
{
    internal IReadOnlySet<string> SourceConverterTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, SunderRpcCSharpSourceTypeModel> SourceModels { get; init; } = new Dictionary<string, SunderRpcCSharpSourceTypeModel>(StringComparer.Ordinal);
    internal static bool UsesNaturalResources(SunderRpcCSharpCompiledContract contract)
        => contract.ModelProjection.ServiceReferences.Count != 0 || contract.SourceTypes.SelectMany(type => type.Properties).Any(property => HasContent(property.Source.Type))
            || contract.Methods.Any(method => method.Source.Parameters.Any(parameter => HasContent(parameter.Type)) || HasContent(method.Source.ReturnType))
            || contract.ImportedValueLayouts.Keys.Where(contract.Descriptor.Definitions.ContainsKey).Any(key => ContainsContent(contract.Descriptor.Definitions[key]));

    private static bool HasContent(ITypeSymbol type)
        => type is INamedTypeSymbol named && (named.ContainingNamespace.ToDisplayString() == "Sunder.Sdk.Services" && named.Name == "SunderContent"
            && named.Arity == 0 && named.ContainingAssembly.Identity.Name == "Sunder.Sdk"
            || named.TypeArguments.Any(HasContent)) || type is IArrayTypeSymbol array && HasContent(array.ElementType);

    internal bool HasResources => Projection.ServiceReferences.Count != 0 || Descriptor.Definitions.Values.Any(ContainsContent);
    private static bool ContainsContent(System.Text.Json.JsonElement value)
        => value.ValueKind == System.Text.Json.JsonValueKind.Object
            ? value.TryGetProperty("logicalType", out var logical) && logical.ValueKind == System.Text.Json.JsonValueKind.String && logical.GetString() == "content-reference" || value.EnumerateObject().Any(property => ContainsContent(property.Value))
            : value.ValueKind == System.Text.Json.JsonValueKind.Array && value.EnumerateArray().Any(ContainsContent);
    internal SunderRpcCSharpResourceVisitors Visitors { get; } = new(Descriptor, Projection, Layouts,
        Targets.ToDictionary(pair => pair.Key, pair => pair.Value.InterfaceName, StringComparer.Ordinal), "global::" + SchemaNamespace + ".__SunderContract.Descriptor");

    internal static SunderRpcCSharpMarshallingPlan Imported(SunderRpcCSharpImportedContract contract)
    {
        var model = SunderRpcCSharpImportPrepass.GeneratorModel(contract);
        var definitions = model.Definitions.ToDictionary(value => value.DefinitionName, StringComparer.Ordinal);
        var structs = model.Definitions.Where(value => value is SunderRpcCSharpStringEnumDefinitionModel or SunderRpcCSharpNumericEnumDefinitionModel or SunderRpcCSharpTransparentDefinitionModel)
            .Select(value => "global::" + Editor.GeneratedPackageNames.Models(contract.AuthorityPackageId!) + "." + value.TypeName).ToHashSet(StringComparer.Ordinal);
        var layouts = model.DefinitionTypes.ToDictionary(pair => pair.Key, pair => new SunderRpcCSharpResourceLayout(pair.Value, structs.Contains(pair.Value), []), StringComparer.Ordinal);
        foreach (var (key, definition) in definitions)
        {
            var type = key.StartsWith('\0') ? "global::" + Editor.GeneratedPackageNames.Models(contract.AuthorityPackageId!) + "." + definition.TypeName : layouts[key].TypeName;
            var members = definition is SunderRpcCSharpRecordDefinitionModel record ? record.Record.Properties.Select(property => new SunderRpcCSharpResourceMember(property.WireName,
                property.Name, property.Serialization == SunderRpcCSharpPropertySerialization.WhenWritingDefault,
                property.TypeName.EndsWith('?') && structs.Contains(property.TypeName[..^1]), property.TypeName,
                OptionalMember(contract, key, property.WireName))
                { ConstantValue = property.ConstantValue, IgnoreWhenNull = property.Serialization == SunderRpcCSharpPropertySerialization.WhenWritingNull }).ToArray() : [];
            Dictionary<string, string>? variants = null;
            if (definition is SunderRpcCSharpTaggedUnionDefinitionModel union)
            {
                variants = new(StringComparer.Ordinal);
                foreach (var variant in union.Variants)
                {
                    var variantType = variant.TypeName.StartsWith("global::", StringComparison.Ordinal) ? variant.TypeName : "global::" + Editor.GeneratedPackageNames.Models(contract.AuthorityPackageId!) + "." + variant.TypeName;
                    if (variant.SyntheticRecord is { } synthetic)
                    {
                        var variantSource = definition.Schema.GetProperty("oneOf").EnumerateArray()
                            .Single(value => ResolveSchema(contract.Descriptor, value).GetProperty("properties").GetProperty(union.DiscriminatorName).GetProperty("const").GetString() == variant.Tag);
                        var variantSchema = ResolveSchema(contract.Descriptor, variantSource);
                        var variantDefinition = variantSource.TryGetProperty("$ref", out var variantReference) ? Key(variantReference.GetString()!) : key;
                        variants.Add(variantSchema.GetRawText(), variantType);
                        layouts["\0" + synthetic.TypeName] = new(variantType, false, synthetic.Properties.Select(property => new SunderRpcCSharpResourceMember(property.WireName,
                            property.Name, property.Serialization == SunderRpcCSharpPropertySerialization.WhenWritingDefault,
                            property.TypeName.EndsWith('?') && structs.Contains(property.TypeName[..^1]), property.TypeName,
                            OptionalMember(contract, variantDefinition, property.WireName))
                            { ConstantValue = property.ConstantValue, IgnoreWhenNull = property.Serialization == SunderRpcCSharpPropertySerialization.WhenWritingNull }).ToArray());
                    }
                    else variants.Add(definitions.Single(pair => pair.Value.TypeName == variant.TypeName || "global::" + Editor.GeneratedPackageNames.Models(contract.AuthorityPackageId!) + "." + pair.Value.TypeName == variant.TypeName).Key, variantType);
                }
            }
            layouts[key] = new(type, structs.Contains(type), members, definition is SunderRpcCSharpTransparentDefinitionModel ? "Value" : null, variants,
                (definition as SunderRpcCSharpTransparentDefinitionModel)?.ValueTypeName)
            {
                ConverterTypeName = definition switch
                {
                    SunderRpcCSharpStringEnumDefinitionModel value => Converter(value.ConverterTypeName),
                    SunderRpcCSharpNumericEnumDefinitionModel value => Converter(value.ConverterTypeName),
                    SunderRpcCSharpTaggedUnionDefinitionModel value => Converter(value.ConverterTypeName),
                    SunderRpcCSharpTransparentDefinitionModel value => Converter(value.ConverterTypeName),
                    _ => null,
                },
            };
        }
        foreach (var key in contract.ModelProjection.Models.Keys.Where(key => layouts.ContainsKey(key) && !definitions.ContainsKey(key)))
        {
            // Multiple authenticated definition keys can name one canonical model.
            // They reuse its members/transparent shape as well as its CLR name.
            var canonical = definitions.Keys.First(candidate => layouts[candidate].TypeName == layouts[key].TypeName);
            layouts[key] = layouts[canonical];
        }
        return new(contract.Descriptor, contract.ModelProjection, layouts, contract.ResourceTargets, contract.GeneratedLogicalName);

        string Converter(string name) => "global::" + Editor.GeneratedPackageNames.Internal(contract.AuthorityPackageId!) + "." + name;
    }

    private static System.Text.Json.JsonElement ResolveSchema(SunderRpcContractDescriptor descriptor, System.Text.Json.JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference)) schema = descriptor.Definitions[Key(reference.GetString()!)];
        return schema;
    }

    private static bool OptionalMember(SunderRpcCSharpImportedContract contract, string definition, string member)
        => contract.ModelProjection.OptionalServiceReferenceMembers.TryGetValue(definition, out var members)
           && members.Contains(member, StringComparer.Ordinal);

    internal static SunderRpcCSharpMarshallingPlan Owned(SunderRpcCSharpCompiledContract contract)
    {
        var bindings = contract.SourceTypes.ToDictionary(source => Key(source.DefinitionReference), source => new SunderRpcCSharpTypeBinding(
            source.SourceType.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat), null), StringComparer.Ordinal);
        foreach (var (key, layout) in contract.ImportedValueLayouts.Where(pair => contract.Descriptor.Definitions.ContainsKey(pair.Key)))
            bindings.Add(key, new(layout.TypeName, null));
        foreach (var (key, target) in contract.ModelProjection.ServiceReferences)
            bindings[key] = new("global::Sunder.Sdk.Services.ServiceReference<" + contract.ResourceTargets[target.ContractId + "/" + target.ServiceId].InterfaceName + ">", null);
        foreach (var method in contract.Methods)
        {
            var parameters = WireParameters(method.Source);
            bindings[method.RequestDefinition] = new(parameters.Count == 1 ? parameters[0].Type.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat) : Envelope(method.RequestDefinition), null);
            if (!method.ReturnsUnit) bindings[method.OutputDefinition] = new(((INamedTypeSymbol)method.Source.ReturnType).TypeArguments[0].ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat), null);
        }
        // Completion responses are serialized as an empty dictionary, not an
        // authored CLR Unit. A shared no-argument request may already bind the
        // same schema to an emitted envelope; preserve that concrete binding.
        foreach (var method in contract.Methods.Where(method => method.ReturnsUnit))
            bindings.TryAdd(method.OutputDefinition, new("global::System.Collections.Generic.Dictionary<string, object>", null));
        var model = SunderRpcCSharpGeneratorModelBuilder.Build(contract.Descriptor, "internal", bindings, contentTypeName: "global::Sunder.Sdk.Services.SunderContent");
        var layouts = model.DefinitionTypes.ToDictionary(pair => pair.Key, pair => new SunderRpcCSharpResourceLayout(pair.Value, false, []), StringComparer.Ordinal);
        foreach (var (key, layout) in contract.ImportedValueLayouts) layouts[key] = layout;
        foreach (var source in contract.SourceTypes)
            layouts[Key(source.DefinitionReference)] = new(source.SourceType.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat), source.SourceType.IsValueType,
                source.Properties.Select(property => new SunderRpcCSharpResourceMember(property.WireName, SunderRpcCSharpSyntax.EscapeIdentifier(property.Source.Name), property.IsOptional,
                    NullableValue(property.Source.Type), property.Source.Type.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat),
                    SunderRpcCSharpSourceCompiler.IsOptionalReference(property.Source))).ToArray(),
                source.IsTransparent ? SunderRpcCSharpSyntax.EscapeIdentifier(source.Properties.Single().Source.Name) : null,
                source.Union?.Variants.ToDictionary(variant => Key(variant.DefinitionReference), variant => variant.SourceType.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat), StringComparer.Ordinal),
                source.IsTransparent ? source.Properties.Single().Source.Type.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat) : null);
        foreach (var method in contract.Methods)
        {
            var parameters = WireParameters(method.Source);
            if (parameters.Count == 1)
                layouts[method.RequestDefinition] = layouts[method.RequestDefinition] with { ValueType = parameters[0].Type.IsValueType };
            else
                layouts[method.RequestDefinition] = new(Envelope(method.RequestDefinition), false, parameters.Select((parameter, index) => new SunderRpcCSharpResourceMember(
                    WireName(parameter), "P" + index, false, NullableValue(parameter.Type), parameter.Type.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat))).ToArray());
            if (!method.ReturnsUnit)
                layouts[method.OutputDefinition] = layouts[method.OutputDefinition] with { ValueType = ((INamedTypeSymbol)method.Source.ReturnType).TypeArguments[0].IsValueType };
        }
        return new(contract.Descriptor, contract.ModelProjection, layouts, contract.ResourceTargets, contract.GeneratedLogicalName)
        {
            SourceConverterTypes = contract.SourceTypes.Where(source => source.Construction is not null || source.Union is not null || source.IsTransparent || source.SourceType.TypeKind == TypeKind.Enum)
                .Select(source => source.SourceType.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat)).ToHashSet(StringComparer.Ordinal),
            SourceModels = contract.SourceTypes.ToDictionary(source => source.SourceType.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat), StringComparer.Ordinal),
        };
    }

    internal static void EmitEnvelopes(StringBuilder output, SunderRpcCSharpCompiledContract contract)
    {
        foreach (var method in contract.Methods.Where(method => WireParameters(method.Source).Count != 1).DistinctBy(method => method.RequestDefinition))
        {
            output.Append("private sealed class ").Append(Envelope(method.RequestDefinition)).AppendLine(" {");
            foreach (var (parameter, index) in WireParameters(method.Source).Select((value, index) => (value, index)))
                output.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(").Append(Literal(WireName(parameter))).Append(")] public required ")
                    .Append(parameter.Type.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat)).Append(" P").Append(index).AppendLine(" { get; init; }");
            output.AppendLine("}");
        }
    }

    internal void EmitConfiguration(StringBuilder output, bool sourceCodec, bool provider = false)
    {
        output.AppendLine("private void __Configure(global::Sunder.Sdk.Rpc.SunderRpcMarshallingContext context) {");
        if (sourceCodec)
            output.AppendLine("    context.JsonOptions.PropertyNamingPolicy = JsonOptions.PropertyNamingPolicy;")
                .AppendLine("    context.JsonOptions.NumberHandling = JsonOptions.NumberHandling;")
                .AppendLine("    context.JsonOptions.UnmappedMemberHandling = JsonOptions.UnmappedMemberHandling;")
                .AppendLine("    context.JsonOptions.TypeInfoResolver = JsonOptions.TypeInfoResolver;")
                .AppendLine("    for (var index = JsonOptions.Converters.Count - 1; index >= 0; index--) if (JsonOptions.Converters[index] is not global::Sunder.Sdk.Rpc.SunderRpcJsonConverterFactory) context.JsonOptions.Converters.Insert(0, JsonOptions.Converters[index]);");
        else output.AppendLine("    context.JsonOptions.TypeInfoResolver = new __SunderJsonResolver();");
        foreach (var reference in Projection.ServiceReferences.Values.Distinct())
        {
            var target = Targets[reference.ContractId + "/" + reference.ServiceId];
            // The neutral fixed-point pass has the validated interface/schema,
            // while concrete catalogs are emitted with their consuming role.
            // Resolve the role's generated factory rather than naming a later type.
            var factory = SunderRpcCSharpServiceApiEmitter.ReferenceFactoryApiType(target.InterfaceName);
            output.Append("    context.AddServiceReference<").Append(target.InterfaceName).Append(">(global::").Append(target.SchemaNamespace)
                .Append(".__SunderContract.Descriptor, ").Append(Literal(reference.ServiceId)).Append(", ")
                .Append(provider ? "(__bindings ?? throw new global::System.InvalidOperationException(\"Service references require the receiver's shared binding lifetime.\"))" : "__lifetime.Bindings")
                .Append(", (_, provider, retirement) => ((")
                .Append(factory).Append(")(__services?.GetService(typeof(").Append(factory)
                .AppendLine(")) ?? throw new global::System.InvalidOperationException(\"The referenced service requires reviewed receiver-role use and generated DI composition.\")))(provider, retirement));");
        }
        output.AppendLine("}");
        if (!sourceCodec) SunderRpcCSharpJsonMetadata.Emit(output, this);
        Visitors.Emit(output);
    }

    internal string Serialize(string definition, string value, string context = "__marshal", string token = "token", bool responseStream = false)
        => context + (responseStream ? ".SerializeResponseStreamItemAsync<" : ".SerializeAsync<") + Layouts[definition].TypeName + ">(" + value + ", " + (Visitors.Send(definition) ?? "null") + ", " + token + ")";
    internal string Deserialize(string definition, string value, string context = "__marshal", string token = "token")
        => context + ".DeserializeAsync<" + Layouts[definition].TypeName + ">(" + value + ", " + (Visitors.Receive(definition) ?? "null") + ", " + token + ")";
    internal static string Envelope(string definition) => "__Request_" + SunderRpcCSharpSyntax.Identifier(definition).TrimStart('@');
    internal static IReadOnlyList<IParameterSymbol> WireParameters(IMethodSymbol method) => method.Parameters.Where(parameter => !Editor.ServiceApiSyntax.IsContext(parameter)
        && parameter.Type.ToDisplayString() != "System.Threading.CancellationToken").ToArray();
    internal static string WireName(IParameterSymbol parameter) => SunderRpcCSharpNaming.TryCreate(parameter.Name, null, out var name) ? name!.Camel : throw new InvalidOperationException("Invalid wire parameter name.");
    private static bool NullableValue(ITypeSymbol type) => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    private static string Key(string reference) => reference["#/$defs/".Length..];
    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
