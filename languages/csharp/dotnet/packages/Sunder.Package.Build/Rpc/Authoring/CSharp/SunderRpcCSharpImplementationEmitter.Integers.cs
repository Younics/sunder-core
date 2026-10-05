using System.Text;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImplementationEmitter
{
    private static void EmitIntegerConverters(StringBuilder output, string containerName)
    {
        output.Append("        static ").Append(containerName).AppendLine("()")
            .AppendLine("        {");
        // Leave long with the SDK safe-integer converter that these narrower converters read through.
        foreach (var typeName in new[] { "byte", "sbyte", "short", "ushort", "int" })
        {
            output.Append("            JsonOptions.Converters.Add(new PrimitiveIntegerConverter<")
                .Append(typeName).AppendLine(">());");
        }
        output.AppendLine("        }")
            .AppendLine()
            .AppendLine("        private sealed class PrimitiveIntegerConverter<T> : global::System.Text.Json.Serialization.JsonConverter<T>")
            .AppendLine("            where T : struct, global::System.Numerics.IBinaryInteger<T>")
            .AppendLine("        {")
            .AppendLine("            public override T Read(")
            .AppendLine("                ref global::System.Text.Json.Utf8JsonReader reader,")
            .AppendLine("                global::System.Type typeToConvert,")
            .AppendLine("                global::System.Text.Json.JsonSerializerOptions options)")
            .AppendLine("            {")
            .AppendLine("                var value = global::System.Text.Json.JsonSerializer.Deserialize<long>(ref reader, options);")
            .AppendLine("                try")
            .AppendLine("                {")
            .AppendLine("                    return T.CreateChecked(value);")
            .AppendLine("                }")
            .AppendLine("                catch (global::System.OverflowException exception)")
            .AppendLine("                {")
            .AppendLine("                    throw new global::System.Text.Json.JsonException($\"An RPC integer is outside the CLR range of {typeof(T).Name}.\", exception);")
            .AppendLine("                }")
            .AppendLine("            }")
            .AppendLine()
            .AppendLine("            public override void Write(")
            .AppendLine("                global::System.Text.Json.Utf8JsonWriter writer,")
            .AppendLine("                T value,")
            .AppendLine("                global::System.Text.Json.JsonSerializerOptions options)")
            .AppendLine("                => writer.WriteNumberValue(long.CreateChecked(value));")
            .AppendLine("        }")
            .AppendLine();
    }
}
