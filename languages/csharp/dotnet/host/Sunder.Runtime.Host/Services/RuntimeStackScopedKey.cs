using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeStackScopedKey
{
    public const string ActionKind = "action";
    public const string InputKind = "input";
    public const string RemapKind = "remap";

    public static string Create(string kind, string ownerPackageId, string contributorId, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contributorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new[] { ownerPackageId, contributorId, localId });
        return kind + ":" + Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryParse(
        string expectedKind,
        string key,
        out string ownerPackageId,
        out string contributorId,
        out string localId)
    {
        ownerPackageId = string.Empty;
        contributorId = string.Empty;
        localId = string.Empty;
        var prefix = expectedKind + ":";
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encoded = key[prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var values = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(encoded));
            if (values is not { Length: 3 } || values.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }
            ownerPackageId = values[0];
            contributorId = values[1];
            localId = values[2];
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }
}
