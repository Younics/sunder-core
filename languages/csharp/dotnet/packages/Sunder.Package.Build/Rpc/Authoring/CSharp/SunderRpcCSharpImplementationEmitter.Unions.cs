using System.Text;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImplementationEmitter
{
    private static void EmitUnionSchemaHolder(StringBuilder output, SunderRpcCSharpCompiledContract contract)
    {
        if (!contract.SourceTypes.Any(static type => type.Union is not null)) return;
        // Emit once outside the provider loop, so provider count cannot multiply a 4 MiB descriptor.
        output.Append("namespace ").Append(EscapeNamespace(contract.GeneratedLogicalName)).AppendLine()
            .AppendLine("{")
            .AppendLine("    internal static class __SunderSourceUnionSchema")
            .AppendLine("    {")
            .AppendLine("        private static readonly global::System.Lazy<global::Sunder.Sdk.Rpc.SunderRpcContractDescriptor> Schema = new(static () =>")
            .Append("            global::Sunder.Sdk.Rpc.SunderRpcContractDescriptor.Parse(global::System.Text.Encoding.UTF8.GetBytes(\"")
            .Append(EscapeString(Encoding.UTF8.GetString(contract.Descriptor.GetCanonicalUtf8()))).AppendLine("\")));")
            .AppendLine()
            .AppendLine("        internal static global::Sunder.Sdk.Rpc.SunderRpcContractDescriptor Value => Schema.Value;")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine();
    }

    private static void EmitUnionConverters(StringBuilder output, SunderRpcCSharpCompiledContract contract)
    {
        if (!contract.SourceTypes.Any(static type => type.Union is not null)) return;

        output.AppendLine("        [global::System.ThreadStatic]")
            .AppendLine("        private static int UnionCodecDepth;")
            .AppendLine()
            .AppendLine("        private static void EnterUnionCodec(global::System.Text.Json.JsonSerializerOptions options)")
            .AppendLine("        {")
            // SerializeToElement starts a fresh serializer depth counter, so nested union
            // converters need a shared synchronous guard against cyclic runtime values.
            .AppendLine("            if (UnionCodecDepth >= (options.MaxDepth == 0 ? 64 : options.MaxDepth))")
            .AppendLine("                throw new global::System.Text.Json.JsonException(\"The RPC union codec depth budget was exhausted.\");")
            .AppendLine("            UnionCodecDepth++;")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        private static global::System.Text.Json.JsonElement ValidateUnionValue(")
            .AppendLine("            global::System.Text.Json.JsonElement value, string schemaReference)")
            .AppendLine("        {")
            .Append("            if (!global::").Append(EscapeNamespace(contract.GeneratedLogicalName))
            .AppendLine(".__SunderSourceUnionSchema.Value.IsValid(schemaReference, value, out var error))")
            .AppendLine("                throw new global::System.Text.Json.JsonException(error ?? \"The RPC union value is invalid.\");")
            .AppendLine("            return value;")
            .AppendLine("        }")
            .AppendLine();

        for (var index = 0; index < contract.SourceTypes.Count; index++)
        {
            var type = contract.SourceTypes[index];
            if (type.Union is not { } union) continue;
            var typeName = type.SourceType.ToDisplayString(TypeDisplayFormat);
            output.Append("        private sealed class UnionConverter").Append(index)
                .Append(" : global::System.Text.Json.Serialization.JsonConverter<").Append(typeName).AppendLine(">")
                .AppendLine("        {");
            if (union.AcceptsNull)
            {
                output.AppendLine("            public override bool HandleNull => true;");
            }
            output.AppendLine("            private static int SelectVariant(global::System.Text.Json.JsonElement value)")
                .AppendLine("            {")
                // Validate the union first: a failed/budget-exhausted branch must never turn
                // an ambiguous or invalid union into an apparently unique match.
                .Append("                ValidateUnionValue(value, \"").Append(EscapeString(type.DefinitionReference)).AppendLine("\");")
                .Append("                var schema = global::").Append(EscapeNamespace(contract.GeneratedLogicalName))
                .AppendLine(".__SunderSourceUnionSchema.Value;")
                .AppendLine("                var selected = -1;");
            for (var variantIndex = 0; variantIndex < union.Variants.Count; variantIndex++)
            {
                output.Append("                if (schema.IsValid(\"").Append(EscapeString(union.Variants[variantIndex].DefinitionReference))
                    .AppendLine("\", value, out _))")
                    .AppendLine("                {")
                    .AppendLine("                    if (selected >= 0)")
                    .AppendLine("                        throw new global::System.Text.Json.JsonException(\"The RPC union value matches more than one variant.\");")
                    .Append("                    selected = ").Append(variantIndex).AppendLine(";")
                    .AppendLine("                }");
            }
            output.AppendLine("                if (selected < 0)")
                .AppendLine("                    throw new global::System.Text.Json.JsonException(\"The RPC union value matches no declared variant.\");")
                .AppendLine("                return selected;")
                .AppendLine("            }")
                .AppendLine()
                .Append("            public override ").Append(typeName).AppendLine(" Read(")
                .AppendLine("                ref global::System.Text.Json.Utf8JsonReader reader,")
                .AppendLine("                global::System.Type typeToConvert,")
                .AppendLine("                global::System.Text.Json.JsonSerializerOptions options)")
                .AppendLine("            {")
                .AppendLine("                EnterUnionCodec(options);")
                .AppendLine("                try")
                .AppendLine("                {")
                .AppendLine("                    using var document = global::System.Text.Json.JsonDocument.ParseValue(ref reader);")
                .AppendLine("                    var value = document.RootElement;")
                .AppendLine("                    var selected = SelectVariant(value);")
                .AppendLine("                    return selected switch")
                .AppendLine("                    {");
            for (var variantIndex = 0; variantIndex < union.Variants.Count; variantIndex++)
            {
                output.Append("                        ").Append(variantIndex)
                    .Append(" => global::System.Text.Json.JsonSerializer.Deserialize<")
                    .Append(union.Variants[variantIndex].SourceType.ToDisplayString(TypeDisplayFormat)).AppendLine(">(value, options)")
                    .AppendLine("                            ?? throw new global::System.Text.Json.JsonException(\"An RPC union variant deserialized to null.\"),");
            }
            output.AppendLine("                        _ => throw new global::System.Text.Json.JsonException(\"The RPC union variant is invalid.\"),")
                .AppendLine("                    };")
                .AppendLine("                }")
                .AppendLine("                finally { UnionCodecDepth--; }")
                .AppendLine("            }")
                .AppendLine()
                .AppendLine("            public override void Write(")
                .AppendLine("                global::System.Text.Json.Utf8JsonWriter writer,")
                .Append("                ").Append(typeName).AppendLine(" value,")
                .AppendLine("                global::System.Text.Json.JsonSerializerOptions options)")
                .AppendLine("            {")
                .AppendLine("                EnterUnionCodec(options);")
                .AppendLine("                try")
                .AppendLine("                {")
                .AppendLine("                    global::System.Text.Json.JsonElement payload;")
                .AppendLine("                    int expected;")
                .AppendLine("                    switch (value)")
                .AppendLine("                    {");
            for (var variantIndex = 0; variantIndex < union.Variants.Count; variantIndex++)
            {
                var variant = union.Variants[variantIndex];
                output.Append("                        case ").Append(variant.SourceType.ToDisplayString(TypeDisplayFormat))
                    .Append(" variant").Append(variantIndex).AppendLine(":")
                    .AppendLine("                        {")
                    .Append("                            payload = global::System.Text.Json.JsonSerializer.SerializeToElement(variant")
                    .Append(variantIndex).AppendLine(", options);")
                    .Append("                            expected = ").Append(variantIndex).AppendLine(";")
                    .AppendLine("                            break;")
                    .AppendLine("                        }");
            }
            output.AppendLine("                        default:")
                .AppendLine("                            throw new global::System.Text.Json.JsonException(\"The RPC union runtime type is not a declared sealed variant.\");")
                .AppendLine("                    }")
                .AppendLine("                    if (SelectVariant(payload) != expected)")
                .AppendLine("                        throw new global::System.Text.Json.JsonException(\"The RPC union payload does not match its runtime variant.\");")
                .AppendLine("                    payload.WriteTo(writer);")
                .AppendLine("                }")
                .AppendLine("                finally { UnionCodecDepth--; }")
                .AppendLine("            }")
                .AppendLine("        }")
                .AppendLine();
        }
    }
}
