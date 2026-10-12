using System.Text;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImplementationEmitter
{
    private static void EmitConstructorConverters(StringBuilder output, SunderRpcCSharpCompiledContract contract)
    {
        for (var index = 0; index < contract.SourceTypes.Count; index++)
        {
            var type = contract.SourceTypes[index];
            if (type.Construction is not { } construction) continue;
            var name = type.SourceType.ToDisplayString(TypeDisplayFormat);
            output.Append("        private sealed class ConstructorConverter").Append(index)
                .Append(" : global::System.Text.Json.Serialization.JsonConverter<").Append(name).AppendLine(">")
                .AppendLine("        {")
                .Append("            public override ").Append(name).AppendLine(" Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)")
                .AppendLine("            {")
                .AppendLine("                EnterUnionCodec(options);")
                .AppendLine("                try")
                .AppendLine("                {")
                .AppendLine("                    using var document = global::System.Text.Json.JsonDocument.ParseValue(ref reader);")
                .Append("                    var value = ValidateUnionValue(document.RootElement, \"").Append(EscapeString(type.DefinitionReference)).AppendLine("\");");
            for (var propertyIndex = 0; propertyIndex < type.Properties.Count; propertyIndex++)
            {
                var property = type.Properties[propertyIndex];
                var propertyType = property.Source.Type.ToDisplayString(TypeDisplayFormat);
                output.Append("                    var p").Append(propertyIndex).Append(" = ");
                if (property.IsOptional)
                    output.Append("value.TryGetProperty(\"").Append(EscapeString(property.WireName)).Append("\", out var member").Append(propertyIndex).Append(") ? ");
                output.Append("global::System.Text.Json.JsonSerializer.Deserialize<").Append(propertyType).Append(">(");
                if (property.IsOptional) output.Append("member").Append(propertyIndex);
                else output.Append("value.GetProperty(\"").Append(EscapeString(property.WireName)).Append("\")");
                output.Append(", options)!");
                if (property.IsOptional) output.Append(" : default(").Append(propertyType).Append(')');
                output.AppendLine(";");
            }
            output.Append("                    return new ").Append(name).Append('(')
                .Append(string.Join(", ", construction.Parameters.Select((property, parameter) => property is null
                    ? ConstructorDefault(construction.Constructor.Parameters[parameter]) : "p" + PropertyIndex(property)))).AppendLine(")")
                .AppendLine("                    {");
            foreach (var property in construction.Initializers)
                output.Append("                        ").Append(EscapeIdentifier(property.Name)).Append(" = p").Append(PropertyIndex(property)).AppendLine(",");
            output.AppendLine("                    };")
                .AppendLine("                }")
                .AppendLine("                finally { UnionCodecDepth--; }")
                .AppendLine("            }")
                .Append("            public override void Write(global::System.Text.Json.Utf8JsonWriter writer, ").Append(name).AppendLine(" value, global::System.Text.Json.JsonSerializerOptions options)")
                .AppendLine("            {")
                .AppendLine("                EnterUnionCodec(options);")
                .AppendLine("                try")
                .AppendLine("                {")
                .AppendLine("                    writer.WriteStartObject();");
            if (type.SyntheticTag is { } tag)
                output.Append("                    writer.WriteString(\"").Append(EscapeString(tag.PropertyName)).Append("\", \"").Append(EscapeString(tag.Tag)).AppendLine("\");");
            for (var propertyIndex = 0; propertyIndex < type.Properties.Count; propertyIndex++)
            {
                var property = type.Properties[propertyIndex];
                output.Append("                    var p").Append(propertyIndex).Append(" = value.").Append(EscapeIdentifier(property.Source.Name)).AppendLine(";");
                if (property.IsOptional) output.Append("                    if (p").Append(propertyIndex).AppendLine(".IsPresent)");
                output.AppendLine("                    {")
                    .Append("                        writer.WritePropertyName(\"").Append(EscapeString(property.WireName)).AppendLine("\");")
                    .Append("                        global::System.Text.Json.JsonSerializer.Serialize(writer, p").Append(propertyIndex).AppendLine(", options);")
                    .AppendLine("                    }");
            }
            output.AppendLine("                    writer.WriteEndObject();")
                .AppendLine("                }")
                .AppendLine("                finally { UnionCodecDepth--; }")
                .AppendLine("            }")
                .AppendLine("        }");

            int PropertyIndex(IPropertySymbol property)
                => Enumerable.Range(0, type.Properties.Count).Single(value => SymbolEqualityComparer.Default.Equals(type.Properties[value].Source, property));
        }
    }

    private static string ConstructorDefault(IParameterSymbol parameter)
    {
        var type = parameter.Type.ToDisplayString(TypeDisplayFormat);
        if (parameter.ExplicitDefaultValue is null) return "default(" + type + ")!";
        var literal = parameter.ExplicitDefaultValue switch
        {
            string value => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true),
            char value => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true),
            bool value => value ? "true" : "false",
            uint value => value.ToString(CultureInfo.InvariantCulture) + "U",
            long value => value.ToString(CultureInfo.InvariantCulture) + "L",
            ulong value => value.ToString(CultureInfo.InvariantCulture) + "UL",
            decimal value => value.ToString(CultureInfo.InvariantCulture) + "M",
            float value when float.IsNaN(value) => "global::System.Single.NaN",
            float value when float.IsPositiveInfinity(value) => "global::System.Single.PositiveInfinity",
            float value when float.IsNegativeInfinity(value) => "global::System.Single.NegativeInfinity",
            float value => value.ToString("R", CultureInfo.InvariantCulture) + "F",
            double value when double.IsNaN(value) => "global::System.Double.NaN",
            double value when double.IsPositiveInfinity(value) => "global::System.Double.PositiveInfinity",
            double value when double.IsNegativeInfinity(value) => "global::System.Double.NegativeInfinity",
            double value => value.ToString("R", CultureInfo.InvariantCulture) + "D",
            sbyte or byte or short or ushort or int => Convert.ToString(parameter.ExplicitDefaultValue, CultureInfo.InvariantCulture)!,
            _ => throw new InvalidOperationException("The validated ignored constructor parameter has no representable default."),
        };
        return "(" + type + ")(" + literal + ")";
    }

    private static void EmitDateTimeOffsetConverter(StringBuilder output)
        => output.AppendLine("""
                    private sealed class SourceDateTimeOffsetConverter : global::System.Text.Json.Serialization.JsonConverter<global::System.DateTimeOffset>
                    {
                        public override global::System.DateTimeOffset Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)
                        {
                            if (reader.TokenType == global::System.Text.Json.JsonTokenType.String
                                && global::Sunder.Sdk.Rpc.SunderRpcOffsetDateTime.TryParse(reader.GetString(), out var wire)
                                && wire.TryGetDateTimeOffset(out var value)) return value;
                            throw new global::System.Text.Json.JsonException("An RPC offset date-time must preserve its offset and be exactly representable in DateTimeOffset ticks.");
                        }
                        public override void Write(global::System.Text.Json.Utf8JsonWriter writer, global::System.DateTimeOffset value, global::System.Text.Json.JsonSerializerOptions options)
                        {
                            var text = value.ToString(value.Offset == global::System.TimeSpan.Zero
                                ? "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", global::System.Globalization.CultureInfo.InvariantCulture);
                            writer.WriteStringValue(new global::Sunder.Sdk.Rpc.SunderRpcOffsetDateTime(text).Value);
                        }
                    }
            """);
}
