using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcDescriptorParser
{
    private static void ValidateTokenStream(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
        });
        var objects = new Stack<ObjectNames?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new ObjectNames());
                    break;
                case JsonTokenType.StartArray:
                    objects.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var names = objects.Peek()
                                ?? throw Error("A JSON property appeared outside an object.");
                    var propertyName = reader.GetString()!;
                    ValidateUnicodeScalars(propertyName);
                    if (!names.Exact.Add(propertyName))
                    {
                        throw Error($"Duplicate JSON property '{propertyName}' is not allowed.", "descriptor-json");
                    }
                    if (!names.IgnoreCase.Add(propertyName))
                    {
                        throw Error($"Case-colliding JSON property '{propertyName}' is not allowed.", "descriptor-json");
                    }
                    break;
                case JsonTokenType.String:
                    ValidateUnicodeScalars(reader.GetString()!);
                    break;
                case JsonTokenType.Number:
                    var token = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
                    var text = Encoding.ASCII.GetString(token);
                    if (!double.TryParse(
                            text,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var finiteNumber)
                        || !double.IsFinite(finiteNumber))
                    {
                        throw Error(
                            $"Descriptor number '{text}' is not a finite IEEE-754 binary64 value.",
                            "descriptor-number");
                    }
                    break;
            }
        }
    }

    private static void ValidateUnicodeScalars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index])
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[++index]))
            {
                throw Error("JSON strings must contain only Unicode scalar values.", "descriptor-json");
            }
        }
    }

    internal static int CountUnicodeScalars(string value)
        => value.EnumerateRunes().Count();

    internal static JsonElement Required(JsonElement element, string name, string path, params JsonValueKind[] kinds)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Error($"{path} is missing required property '{name}'.");
        }
        if (kinds.Length > 0 && !kinds.Contains(value.ValueKind))
        {
            throw Error($"{path}.{name} has unsupported JSON kind '{value.ValueKind}'.");
        }
        return value;
    }

    internal static string RequiredString(JsonElement element, string name, string path, int maximumLength)
    {
        var value = Required(element, name, path, JsonValueKind.String).GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            throw Error($"{path}.{name} must be non-empty and at most {maximumLength} characters.");
        }
        return value;
    }

    internal static void RequireOnlyProperties(JsonElement element, string path, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
            {
                throw Error($"{path} contains unsupported property '{property.Name}'.");
            }
        }
    }

    internal static void RequireKind(JsonElement element, JsonValueKind kind, string path, string description)
    {
        if (element.ValueKind != kind)
        {
            throw Error($"{path} must be {description}.");
        }
    }

    internal static SunderRpcDescriptorException Error(
        string message,
        string errorCode = "descriptor-representation")
        => new(
            errorCode,
            message.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal));

    private static SunderRpcDescriptorException Error(
        string message,
        Exception innerException,
        string errorCode = "descriptor-representation")
        => new(errorCode, message, innerException);

    private static bool IsMemberId(string value)
        => value.Length is > 0 and <= 128
           && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value.All(static character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.' or '-' or '_');

    private static bool IsContractId(string value)
        => value.Length is > 0 and <= 128
           && value[0] != '.'
           && value[^1] != '.'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.All(static character => character is >= 'a' and <= 'z'
               or >= '0' and <= '9'
               or '.');

    private static bool IsDefinitionName(string value)
        => value.Length is > 0 and <= 128
           && value.All(static character => char.IsAsciiLetterOrDigit(character)
               || character is '.' or '-' or '_');

    private sealed class ObjectNames
    {
        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoreCase { get; } = new(AsciiCaseInsensitiveComparer.Instance);
    }

    private sealed class AsciiCaseInsensitiveComparer : IEqualityComparer<string>
    {
        public static AsciiCaseInsensitiveComparer Instance { get; } = new();

        public bool Equals(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null || left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (Fold(left[index]) != Fold(right[index])) return false;
            }
            return true;
        }

        public int GetHashCode(string value)
        {
            var hash = new HashCode();
            foreach (var character in value) hash.Add(Fold(character));
            return hash.ToHashCode();
        }

        private static char Fold(char value)
            => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
    }
}
