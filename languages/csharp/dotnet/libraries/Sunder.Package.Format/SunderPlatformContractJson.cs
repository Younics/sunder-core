using System.Text.Json;

namespace Sunder.Package.Format;

internal static class SunderPlatformContractJson
{
    internal const int MaximumBytes = 64 * 1024 * 1024;

    internal static string ReadString(JsonElement root, string member, int maximumLength)
    {
        var element = root.GetProperty(member);
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Platform contract member '{member}' must be a string.");
        var value = element.GetString()!;
        if (value.Length == 0 || value.Length > maximumLength)
            throw new InvalidDataException($"Platform contract member '{member}' has an invalid length.");
        return value;
    }

    internal static void RequireString(JsonElement root, string member, string expected)
    {
        if (ReadString(root, member, expected.Length) != expected)
            throw new InvalidDataException($"Platform contract member '{member}' must be '{expected}'.");
    }

    internal static long ReadInteger(JsonElement root, string member, long minimum, long maximum)
    {
        var element = root.GetProperty(member);
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var value) || value < minimum || value > maximum)
            throw new InvalidDataException($"Platform contract member '{member}' must be an integer in {minimum}..{maximum}.");
        return value;
    }

    internal static void RequireArray(JsonElement element, int minimum, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() < minimum || element.GetArrayLength() > maximum)
            throw new InvalidDataException($"A platform contract array must contain {minimum}..{maximum} entries.");
    }

    internal static string ReadDigest(JsonElement root, string member)
    {
        var value = ReadString(root, member, 64);
        if (value.Length != 64 || value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"Platform contract member '{member}' must be lowercase SHA-256 hexadecimal.");
        return value;
    }
}
