using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

// Ordinary RPC metadata is closed over the reviewed wire graph. Do not ask the
// default reflection resolver to build DTO/collection accessors: its process-wide
// accessor cache can retain a collectible package after its receiver has drained.
internal static class SunderRpcCSharpJsonMetadata
{
    private const string Metadata = "global::System.Text.Json.Serialization.Metadata.";

    internal static void Emit(StringBuilder output, SunderRpcCSharpMarshallingPlan plan)
    {
        var layouts = plan.Layouts.GroupBy(pair => Normalize(pair.Value.TypeName, plan), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(pair => pair.Value.Members.Count).First(), StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layout in plan.Layouts.Values)
        {
            Add(layout.TypeName);
            foreach (var member in layout.Members) Add(member.TypeName);
            if (layout.TransparentTypeName is { } transparent) Add(transparent);
            foreach (var variant in layout.Variants?.Values ?? []) Add(variant);
        }
        // Converter internals and unit responses use these fixed, non-package types.
        foreach (var type in new[] { "string", "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "object",
                     "global::System.Guid", "global::System.DateOnly", "global::System.DateTimeOffset", "global::System.Numerics.BigInteger",
                     "global::System.Text.Json.JsonElement", "byte[]", "global::Sunder.Sdk.Rpc.SunderRpcContentReference",
                     "global::System.Collections.Generic.Dictionary<string, object>" }) Add(type);

        output.AppendLine("private sealed class __SunderJsonResolver : global::System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver {")
            .AppendLine("    public global::System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(global::System.Type type, global::System.Text.Json.JsonSerializerOptions options) {");
        var records = new List<(string Type, SunderRpcCSharpResourceLayout Layout)>();
        foreach (var type in types.Order(StringComparer.Ordinal))
        {
            output.Append("        if (type == typeof(").Append(type).AppendLine(")) {");
            layouts.TryGetValue(type, out var entry);
            plan.SourceModels.TryGetValue(type, out var sourceModel);
            var customAttribute = sourceModel?.SourceType.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConverterAttribute");
            var authoredConverter = customAttribute?.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
            var converter = entry.Value?.ConverterTypeName ?? authoredConverter?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var generic = SyntaxFactory.ParseTypeName(type).DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault();
            var arguments = generic?.TypeArgumentList.Arguments ?? default;
            if (authoredConverter is not null && sourceModel?.SyntheticTag is not null)
                output.AppendLine("            throw new global::System.Text.Json.JsonException(\"A custom RPC variant codec collides with its synthesized discriminator.\");");
            else if (converter is not null)
                output.Append("            return ").Append(Metadata).Append("JsonMetadataServices.CreateValueInfo<").Append(type).Append(">(options, ")
                    .Append(authoredConverter?.BaseType?.Name == "JsonConverterFactory" ? "(global::System.Text.Json.Serialization.JsonConverter<" + type + ">)" : "")
                    .Append("new ").Append(converter).Append("()")
                    .Append(authoredConverter?.BaseType?.Name == "JsonConverterFactory" ? ".CreateConverter(type, options)" : "").AppendLine(");");
            else if (generic?.Identifier.ValueText == "IReadOnlyList")
                output.Append("            return ").Append(Metadata).Append("JsonMetadataServices.CreateIEnumerableInfo<").Append(type).Append(", ").Append(arguments![0])
                    .Append(">(options, new() { ObjectCreator = static () => new global::System.Collections.Generic.List<").Append(arguments[0]).AppendLine(">() });");
            else if (generic?.Identifier.ValueText is "IReadOnlyDictionary" or "Dictionary")
                output.Append("            return ").Append(Metadata).Append("JsonMetadataServices.Create").Append(generic.Identifier.ValueText == "Dictionary" ? "Dictionary" : "IReadOnlyDictionary")
                    .Append("Info<").Append(type).Append(", ").Append(arguments![0]).Append(", ").Append(arguments[1])
                    .Append(">(options, new() { ObjectCreator = static () => new global::System.Collections.Generic.Dictionary<").Append(arguments[0]).Append(", ").Append(arguments[1]).AppendLine(">() });");
            else if (entry.Value is not null && IsRecord(entry.Key, entry.Value, plan)
                     && !plan.SourceConverterTypes.Contains(type))
            {
                var index = records.Count;
                records.Add((type, entry.Value));
                output.Append("            return Record").Append(index).AppendLine("(options);");
            }
            else
                // This API creates value metadata directly and honors the actual
                // operation's converters (notably content/reference converters).
                // Unlike DefaultJsonTypeInfoResolver it does not reflect records.
                output.Append("            return Value<").Append(type).AppendLine(">(options);");
            output.AppendLine("        }");
        }
        output.AppendLine("        throw new global::System.NotSupportedException(\"The type is not in the generated RPC JSON graph: \" + type);")
            .AppendLine("    }")
            .AppendLine("    private static global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> Value<T>(global::System.Text.Json.JsonSerializerOptions options) {")
            .AppendLine("        var info = global::System.Text.Json.Serialization.Metadata.JsonTypeInfo.CreateJsonTypeInfo<T>(options);")
            .AppendLine("        if (info.Kind != global::System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.None) throw new global::System.NotSupportedException(\"Missing generated RPC metadata for \" + typeof(T));")
            .AppendLine("        return info;")
            .AppendLine("    }");
        for (var index = 0; index < records.Count; index++) EmitRecord(output, records[index].Type, records[index].Layout, index, plan);
        output.AppendLine("}");

        void Add(string name)
        {
            name = Normalize(name, plan);
            if (!types.Add(name)) return;
            var syntax = SyntaxFactory.ParseTypeName(name);
            if (syntax is NullableTypeSyntax nullable) Add(nullable.ElementType.ToString());
            if (syntax is ArrayTypeSyntax array) Add(array.ElementType.ToString());
            var generic = syntax.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault();
            // A service interface is a locator target, not a serialized DTO.
            if (generic is not null && generic.Identifier.ValueText != "ServiceReference")
                foreach (var argument in generic.TypeArgumentList.Arguments) Add(argument.ToString());
        }
    }

    private static bool IsRecord(string key, SunderRpcCSharpResourceLayout layout, SunderRpcCSharpMarshallingPlan plan)
    {
        if (layout.TransparentMember is not null || layout.Variants is not null) return false;
        if (layout.Members.Count != 0) return true;
        if (!plan.Descriptor.Definitions.TryGetValue(key, out var schema)) return false;
        while (schema.TryGetProperty("$ref", out var reference)) schema = plan.Descriptor.Definitions[reference.GetString()![8..]];
        return schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object"
            && schema.TryGetProperty("properties", out _) && !plan.Projection.ServiceReferences.ContainsKey(key);
    }

    private static string Normalize(string name, SunderRpcCSharpMarshallingPlan plan)
    {
        // typeof disallows outer nullable reference annotations. Nullable value
        // types remain separate wire types; generic argument annotations are legal.
        if (!name.EndsWith('?')) return name;
        var inner = name[..^1];
        return plan.Layouts.Values.Any(layout => layout.TypeName == inner && layout.ValueType)
               || inner is "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "decimal"
               || inner.StartsWith("global::Sunder.Sdk.Rpc.SunderRpcOptional<", StringComparison.Ordinal)
               || inner is "global::Sunder.Sdk.Rpc.SunderRpcOffsetDateTime" or "global::Sunder.Sdk.Rpc.SunderRpcDuration" or "global::Sunder.Sdk.Rpc.SunderRpcUri" or "global::Sunder.Sdk.Rpc.SunderRpcDecimal"
               || inner is "global::System.Guid" or "global::System.DateOnly" or "global::System.DateTimeOffset" or "global::System.Numerics.BigInteger" or "global::System.Text.Json.JsonElement"
            ? name : inner;
    }

    private static void EmitRecord(StringBuilder output, string type, SunderRpcCSharpResourceLayout layout, int index, SunderRpcCSharpMarshallingPlan plan)
    {
        var inaccessible = plan.SourceModels.TryGetValue(type, out var source)
            ? source.Properties.Where(property => property.Source.SetMethod?.DeclaredAccessibility == Accessibility.Private)
                .ToDictionary(property => SunderRpcCSharpSyntax.EscapeIdentifier(property.Source.Name), StringComparer.Ordinal)
            : new Dictionary<string, SunderRpcCSharpSourcePropertyModel>(StringComparer.Ordinal);
        var populated = layout.Members.Where(member => member.ConstantValue is null).ToArray();
        var constructor = source?.SourceType.InstanceConstructors.FirstOrDefault(ctor => ctor.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConstructorAttribute"))
            ?? source?.SourceType.InstanceConstructors.FirstOrDefault(ctor => ctor.DeclaredAccessibility == Accessibility.Public && ctor.Parameters.Length == 0)
            ?? source?.SourceType.InstanceConstructors.SingleOrDefault(ctor => ctor.DeclaredAccessibility == Accessibility.Public);
        var arguments = constructor?.Parameters.Select(parameter =>
        {
            var position = Array.FindIndex(populated, member => string.Equals(member.MemberName.TrimStart('@'), parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (position < 0) throw new InvalidOperationException("A source JSON constructor parameter has no reviewed property.");
            return "(" + parameter.Type.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat) + ")args[" + position + "]!";
        }).ToArray() ?? [];
        var constructorAccessor = constructor is not null && (arguments.Length != 0 || constructor.DeclaredAccessibility == Accessibility.Private);
        if (constructorAccessor)
            output.AppendLine("    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Constructor)]")
                .Append("    private static extern ").Append(type).Append(" Create").Append(index).Append('(')
                .Append(string.Join(", ", constructor!.Parameters.Select((parameter, position) => parameter.Type.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat) + " p" + position))).AppendLine(");");
        foreach (var (member, memberIndex) in layout.Members.Select((member, i) => (member, i)).Where(pair => pair.member.ConstantValue is null))
            output.Append("    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = ")
                .Append(Literal("set_" + member.MemberName.TrimStart('@'))).AppendLine(")]")
                .Append("    private static extern void Set").Append(index).Append('_').Append(memberIndex).Append('(').Append(layout.ValueType ? "ref " : "")
                .Append(source?.Properties.FirstOrDefault(property => property.Source.Name == member.MemberName.TrimStart('@'))?.Source.ContainingType.ToDisplayString(Editor.ServiceApiSyntax.SignatureFormat) ?? type)
                .Append(" value, ").Append(member.TypeName).AppendLine(" property);");
        output.Append("    private static ").Append(Metadata).Append("JsonTypeInfo<").Append(type).Append("> Record").Append(index)
            .AppendLine("(global::System.Text.Json.JsonSerializerOptions options) {")
            .Append("        return ").Append(Metadata).Append("JsonMetadataServices.CreateObjectInfo<").Append(type).AppendLine(">(options, new() {");
        if (populated.Length == 0)
            output.Append("            ObjectCreator = static () => ").Append(constructorAccessor ? "Create" + index : "new " + type).AppendLine("(),");
        else
        {
            output.Append("            ObjectWithParameterizedConstructorCreator = static args => { var instance = ");
            if (constructorAccessor) output.Append("Create").Append(index).Append('(').Append(string.Join(", ", arguments)).AppendLine(");");
            else
            {
                output.Append("new ").Append(type).AppendLine("() {");
                foreach (var (member, memberIndex) in populated.Select((member, i) => (member, i)))
                    if (!inaccessible.ContainsKey(member.MemberName)) output.Append("                ").Append(member.MemberName).Append(" = (").Append(member.TypeName).Append(")args[").Append(memberIndex).AppendLine("]!,");
                output.AppendLine("            };");
            }
            foreach (var (member, memberIndex) in layout.Members.Select((member, i) => (member, i)).Where(pair => pair.member.ConstantValue is null
                         && (constructorAccessor ? pair.member.OptionalWrapper || !constructor!.Parameters.Any(parameter => string.Equals(parameter.Name, pair.member.MemberName.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) : inaccessible.ContainsKey(pair.member.MemberName))))
                output.Append("                Set").Append(index).Append('_').Append(memberIndex).Append('(').Append(layout.ValueType ? "ref " : "").Append("instance, (")
                    .Append(member.TypeName).Append(")args[").Append(Array.IndexOf(populated, member)).AppendLine("]!);");
            output.AppendLine("                return instance; },")
                .AppendLine("            ConstructorParameterMetadataInitializer = static () => new global::System.Text.Json.Serialization.Metadata.JsonParameterInfoValues[] {");
            foreach (var (member, memberIndex) in populated.Select((member, i) => (member, i)))
                output.Append("                new() { Name = ").Append(Literal(member.MemberName.TrimStart('@'))).Append(", ParameterType = typeof(")
                    .Append(Normalize(member.TypeName, plan)).Append("), Position = ").Append(memberIndex).Append(", HasDefaultValue = true, DefaultValue = default(").Append(member.TypeName).AppendLine(") },");
            output.AppendLine("            },");
        }
        output.AppendLine("            PropertyMetadataInitializer = _ => {")
            .Append("                var properties = new ").Append(Metadata).Append("JsonPropertyInfo[").Append(layout.Members.Count + (source?.SyntheticTag is null ? 0 : 1)).AppendLine("];");
        foreach (var (member, memberIndex) in layout.Members.Select((member, i) => (member, i)))
        {
            output.Append("                var p").Append(memberIndex).Append(" = ").Append(Metadata).Append("JsonMetadataServices.CreatePropertyInfo<").Append(member.TypeName).AppendLine(">(options, new() {")
                .Append("                    IsProperty = true, IsPublic = true, DeclaringType = typeof(").Append(type).Append("), PropertyName = ").Append(Literal(member.MemberName.TrimStart('@')))
                .Append(", JsonPropertyName = ").Append(Literal(member.WireName)).AppendLine(",")
                .Append("                    Getter = static value => ((").Append(type).Append(")value).").Append(member.MemberName).AppendLine(",");
            if (member.ConstantValue is { } constant)
                output.Append("                    Setter = static (_, property) => { if (property != ").Append(Literal(constant)).AppendLine(") throw new global::System.Text.Json.JsonException(\"Invalid RPC discriminator.\"); },");
            else output.Append("                    Setter = static (value, property) => Set").Append(index).Append('_').Append(memberIndex).Append('(')
                    .Append(layout.ValueType ? "ref global::System.Runtime.CompilerServices.Unsafe.Unbox<" + type + ">(value)" : "(" + type + ")value")
                    .AppendLine(", property!),");
            // Imported optional nonnullable fields use CLR null for absence;
            // required nullable fields must still emit null and remain required.
            output.Append("                    IgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.")
                .Append(member.IgnoreWhenNull ? "WhenWritingNull" : "Never").AppendLine(",")
                .AppendLine("                });")
                .Append("                p").Append(memberIndex).Append(".IsRequired = ").Append(member.OptionalWrapper || member.IgnoreWhenNull ? "false" : "true").AppendLine(";");
            if (member.OptionalWrapper)
                output.Append("                p").Append(memberIndex).Append(".ShouldSerialize = static (_, value) => ((").Append(member.TypeName).AppendLine(")value!).IsPresent;");
            output.Append("                properties[").Append(memberIndex).Append("] = p").Append(memberIndex).AppendLine(";");
        }
        if (source?.SyntheticTag is { } tag)
            output.Append("                var tag = ").Append(Metadata).Append("JsonMetadataServices.CreatePropertyInfo<string>(options, new() { IsProperty = true, IsPublic = true, DeclaringType = typeof(")
                .Append(type).Append("), PropertyName = ").Append(Literal(tag.PropertyName)).Append(", JsonPropertyName = ").Append(Literal(tag.PropertyName))
                .Append(", Getter = static _ => ").Append(Literal(tag.Tag)).Append(", Setter = static (_, value) => { if (value != ").Append(Literal(tag.Tag))
                .AppendLine(") throw new global::System.Text.Json.JsonException(\"Invalid RPC discriminator.\"); } });")
                .Append("                tag.IsRequired = true; tag.Order = int.MinValue; properties[").Append(layout.Members.Count).AppendLine("] = tag;");
        output.AppendLine("                return properties;")
            .AppendLine("            },")
            .AppendLine("        });")
            .AppendLine("    }");
    }

    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
