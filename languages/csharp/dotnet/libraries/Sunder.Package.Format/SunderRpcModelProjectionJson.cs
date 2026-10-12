using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class SunderRpcModelProjectionJson
{
    internal static InvalidDataException Error(InvalidDataException exception)
        => exception.Message.StartsWith("rpc-authoring.model-projection-invalid:", StringComparison.Ordinal) ? exception
            : new InvalidDataException("rpc-authoring.model-projection-invalid: " + exception.Message, exception);

    internal static byte[] Write(JsonNode node)
    {
        // Wire member names can contain Unicode scalars. Use the existing authoring string
        // writer rather than a serializer's Unicode block list or uppercase control escapes.
        var output = new StringBuilder();
        WriteValue(JsonSerializer.SerializeToElement(node), 0);
        output.Append('\n');
        if (Encoding.UTF8.GetByteCount(output.ToString()) > SunderPackageFormat.MaxRpcModelProjectionBytes)
            throw new InvalidDataException("Model projection exceeds its 4 MiB byte bound.");
        return Encoding.UTF8.GetBytes(output.ToString());

        void WriteValue(JsonElement value, int depth)
        {
            if (output.Length > SunderPackageFormat.MaxRpcModelProjectionBytes || depth > 16)
                throw new InvalidDataException("Model projection exceeds its byte or depth bound.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                output.Append('{');
                var index = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (index++ != 0) output.Append(',');
                    Line(depth + 1); SunderRpcAuthoringJson.AppendString(output, property.Name); output.Append(": ");
                    WriteValue(property.Value, depth + 1);
                }
                if (index != 0) Line(depth);
                output.Append('}');
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                output.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ != 0) output.Append(',');
                    Line(depth + 1); WriteValue(item, depth + 1);
                }
                if (index != 0) Line(depth);
                output.Append(']');
            }
            else if (value.ValueKind == JsonValueKind.String) SunderRpcAuthoringJson.AppendString(output, value.GetString()!);
            else output.Append(value.GetRawText());
        }

        void Line(int depth) => output.Append('\n').Append(' ', depth * 2);
    }

    internal static IEnumerable<JsonProperty> OrderedMap(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetPropertyCount() > 256)
            throw new InvalidDataException("Model projection maps must be objects with at most 256 entries.");
        string? previous = null;
        foreach (var property in value.EnumerateObject())
        {
            if (previous is not null && string.CompareOrdinal(previous, property.Name) >= 0)
                throw new InvalidDataException("Model projection map keys must be strictly ordinally sorted.");
            previous = property.Name;
            yield return property;
        }
    }

    internal static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()!
            : throw new InvalidDataException("Model projection member must be a non-null string.");

    internal static void RequireId(string value)
    {
        if (value is not { Length: > 0 and <= 128 } || !PackageId.TryParse(value, out _))
            throw new InvalidDataException("Model projection identity must use the canonical package-ID grammar.");
    }

    internal static void RequireVersion(string value)
    {
        if (value is not { Length: > 0 and <= 256 } || !SemanticVersion.TryParse(value, out _))
            throw new InvalidDataException("Model projection version must be strict SemVer.");
    }

    internal static void RequireHash(string value)
    {
        if (!PackageContentSignatureValidator.IsLowercaseSha256(value))
            throw new InvalidDataException("Model projection hash must be lowercase SHA-256.");
    }
}
