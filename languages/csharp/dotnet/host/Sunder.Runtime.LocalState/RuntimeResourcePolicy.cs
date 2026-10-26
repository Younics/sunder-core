using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Runtime.LocalState;

public enum RuntimeResourcePolicyPreset
{
    Standard = 1,
    Large = 2,
    Custom = 3,
}

public sealed record RuntimeResourcePolicy
{
    public RuntimeResourcePolicy(
        int maximumRpcValueBytes,
        int maximumValueDepth,
        int maximumValueNodes,
        int maximumObjectMembers,
        int maximumInlineDecodedBytes,
        int maximumContentReferenceBytes)
    {
        RuntimeResourcePolicies.ValidateValues(
            maximumRpcValueBytes,
            maximumValueDepth,
            maximumValueNodes,
            maximumObjectMembers,
            maximumInlineDecodedBytes,
            maximumContentReferenceBytes);

        MaximumRpcValueBytes = maximumRpcValueBytes;
        MaximumValueDepth = maximumValueDepth;
        MaximumValueNodes = maximumValueNodes;
        MaximumObjectMembers = maximumObjectMembers;
        MaximumInlineDecodedBytes = maximumInlineDecodedBytes;
        MaximumContentReferenceBytes = maximumContentReferenceBytes;
    }

    public int MaximumRpcValueBytes { get; }

    public int MaximumValueDepth { get; }

    public int MaximumValueNodes { get; }

    public int MaximumObjectMembers { get; }

    public int MaximumInlineDecodedBytes { get; }

    public int MaximumContentReferenceBytes { get; }
}

public sealed record RuntimeResourcePolicySelection
{
    public RuntimeResourcePolicySelection(
        RuntimeResourcePolicyPreset preset,
        RuntimeResourcePolicy customPolicy)
    {
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        Preset = preset;
        CustomPolicy = customPolicy ?? throw new ArgumentNullException(nameof(customPolicy));
    }

    public RuntimeResourcePolicyPreset Preset { get; }

    public RuntimeResourcePolicy CustomPolicy { get; }

    public RuntimeResourcePolicy EffectivePolicy => Preset switch
    {
        RuntimeResourcePolicyPreset.Standard => RuntimeResourcePolicies.Standard,
        RuntimeResourcePolicyPreset.Large => RuntimeResourcePolicies.Large,
        RuntimeResourcePolicyPreset.Custom => CustomPolicy,
        _ => throw new InvalidOperationException($"Unsupported Runtime resource policy preset '{Preset}'."),
    };

    public string EffectiveProfileHash => RuntimeResourcePolicyContract.GetEffectiveProfileHash(EffectivePolicy);
}

public sealed record RuntimeResourcePolicyLaunchPayload
{
    internal RuntimeResourcePolicyLaunchPayload(
        int version,
        string effectiveProfileHash,
        RuntimeResourcePolicy effectivePolicy)
    {
        Version = version;
        EffectiveProfileHash = effectiveProfileHash;
        EffectivePolicy = effectivePolicy;
    }

    public int Version { get; }

    public string EffectiveProfileHash { get; }

    public RuntimeResourcePolicy EffectivePolicy { get; }
}

public static class RuntimeResourcePolicies
{
    public const int Mebibyte = 1024 * 1024;
    public const int Kibibyte = 1024;
    public const int MaximumRpcValueBytes = 64 * Mebibyte;
    public const int MaximumValueDepth = 64;
    public const int MaximumValueNodes = 8_388_608;
    public const int MaximumObjectMembers = 131_072;
    public const int MaximumInlineDecodedBytes = 8 * Mebibyte;
    public const int MaximumContentReferenceBytes = 512 * Mebibyte;

    public static RuntimeResourcePolicy ProtocolCeilings { get; } = new(
        MaximumRpcValueBytes,
        MaximumValueDepth,
        MaximumValueNodes,
        MaximumObjectMembers,
        MaximumInlineDecodedBytes,
        MaximumContentReferenceBytes);

    public static RuntimeResourcePolicy CustomMinimums { get; } = new(
        Mebibyte,
        16,
        16_384,
        256,
        16 * Kibibyte,
        Mebibyte);

    public static RuntimeResourcePolicy Standard { get; } = new(
        32 * Mebibyte,
        64,
        4_194_304,
        65_536,
        4 * Mebibyte,
        256 * Mebibyte);

    public static RuntimeResourcePolicy Large { get; } = new(
        64 * Mebibyte,
        64,
        8_388_608,
        131_072,
        8 * Mebibyte,
        512 * Mebibyte);

    public static RuntimeResourcePolicySelection DefaultSelection { get; } = new(
        RuntimeResourcePolicyPreset.Standard,
        Standard);

    internal static void ValidateValues(
        int maximumRpcValueBytes,
        int maximumValueDepth,
        int maximumValueNodes,
        int maximumObjectMembers,
        int maximumInlineDecodedBytes,
        int maximumContentReferenceBytes)
    {
        ValidateRange(
            maximumRpcValueBytes,
            Mebibyte,
            MaximumRpcValueBytes,
            nameof(RuntimeResourcePolicy.MaximumRpcValueBytes));
        ValidateRange(
            maximumValueDepth,
            16,
            MaximumValueDepth,
            nameof(RuntimeResourcePolicy.MaximumValueDepth));
        ValidateRange(
            maximumValueNodes,
            16_384,
            MaximumValueNodes,
            nameof(RuntimeResourcePolicy.MaximumValueNodes));
        ValidateRange(
            maximumObjectMembers,
            256,
            MaximumObjectMembers,
            nameof(RuntimeResourcePolicy.MaximumObjectMembers));
        ValidateRange(
            maximumInlineDecodedBytes,
            16 * Kibibyte,
            MaximumInlineDecodedBytes,
            nameof(RuntimeResourcePolicy.MaximumInlineDecodedBytes));
        ValidateRange(
            maximumContentReferenceBytes,
            Mebibyte,
            MaximumContentReferenceBytes,
            nameof(RuntimeResourcePolicy.MaximumContentReferenceBytes));
    }

    private static void ValidateRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{parameterName} must be between {minimum} and {maximum}, inclusive.");
        }
    }
}

public static class RuntimeResourcePolicyEnvironment
{
    public const string PolicyFilePathVariable = "SUNDER_RUNTIME_RESOURCE_POLICY_FILE";
    public const string LaunchPayloadVariable = "SUNDER_RUNTIME_RESOURCE_POLICY";

    public static void RemovePolicyVariables(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var key in environment.Keys.Where(IsPolicyVariable).ToArray())
        {
            environment.Remove(key);
        }
    }

    private static bool IsPolicyVariable(string variable)
        => string.Equals(variable, PolicyFilePathVariable, StringComparison.OrdinalIgnoreCase)
           || string.Equals(variable, LaunchPayloadVariable, StringComparison.OrdinalIgnoreCase);
}

public static class RuntimeResourcePolicyContract
{
    public const int SettingsFormatVersion = 1;
    public const int EffectiveProfileFormatVersion = 1;
    public const int LaunchPayloadVersion = 1;
    public const int MaximumSettingsFileBytes = 16 * 1024;
    public const int MaximumLaunchPayloadBytes = 4 * 1024;

    public static byte[] SerializeSettings(
        RuntimeResourcePolicySelection selection,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(selection);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = writeIndented }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SettingsFormatVersion);
            writer.WriteString("preset", GetPresetName(selection.Preset));
            writer.WritePropertyName("custom");
            WritePolicy(writer, selection.CustomPolicy);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static RuntimeResourcePolicySelection ParseSettings(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumSettingsFileBytes)
        {
            throw new InvalidDataException(
                $"Runtime resource policy settings must be between 1 and {MaximumSettingsFileBytes} bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            RequireObjectWithProperties(root, "$", "version", "preset", "custom");
            var version = ReadInt32(root, "version", "$.version");
            if (version != SettingsFormatVersion)
            {
                throw new InvalidDataException(
                    $"Runtime resource policy settings version {version} is not supported.");
            }

            var presetElement = root.GetProperty("preset");
            if (presetElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("$.preset must be a string.");
            }
            var preset = presetElement.GetString() switch
            {
                "standard" => RuntimeResourcePolicyPreset.Standard,
                "large" => RuntimeResourcePolicyPreset.Large,
                "custom" => RuntimeResourcePolicyPreset.Custom,
                var value => throw new InvalidDataException(
                    $"$.preset value '{value ?? "null"}' is not supported."),
            };

            var custom = ReadPolicy(root.GetProperty("custom"), "$.custom");
            return new RuntimeResourcePolicySelection(preset, custom);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Runtime resource policy settings are not valid JSON.", exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    public static string GetEffectiveProfileHash(RuntimeResourcePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var canonical = SerializeEffectiveProfile(policy);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    public static RuntimeResourcePolicyLaunchPayload CreateLaunchPayload(RuntimeResourcePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new RuntimeResourcePolicyLaunchPayload(
            LaunchPayloadVersion,
            GetEffectiveProfileHash(policy),
            policy);
    }

    public static string SerializeLaunchPayload(RuntimeResourcePolicy policy)
    {
        var payload = CreateLaunchPayload(policy);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", payload.Version);
            writer.WriteString("effectiveProfileHash", payload.EffectiveProfileHash);
            writer.WritePropertyName("effectivePolicy");
            WritePolicy(writer, payload.EffectivePolicy);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static RuntimeResourcePolicyLaunchPayload ParseLaunchPayload(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumLaunchPayloadBytes)
        {
            throw new InvalidDataException(
                $"Runtime resource policy launch payload must be between 1 and {MaximumLaunchPayloadBytes} bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            RequireObjectWithProperties(root, "$", "version", "effectiveProfileHash", "effectivePolicy");
            var version = ReadInt32(root, "version", "$.version");
            if (version != LaunchPayloadVersion)
            {
                throw new InvalidDataException(
                    $"Runtime resource policy launch payload version {version} is not supported.");
            }

            var hashElement = root.GetProperty("effectiveProfileHash");
            var hash = hashElement.ValueKind == JsonValueKind.String ? hashElement.GetString() : null;
            var policy = ReadPolicy(root.GetProperty("effectivePolicy"), "$.effectivePolicy");
            var expectedHash = GetEffectiveProfileHash(policy);
            if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Runtime resource policy launch payload effectiveProfileHash does not match effectivePolicy.");
            }

            return new RuntimeResourcePolicyLaunchPayload(version, expectedHash, policy);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Runtime resource policy launch payload is not valid JSON.", exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    private static byte[] SerializeEffectiveProfile(RuntimeResourcePolicy policy)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", EffectiveProfileFormatVersion);
            WritePolicyProperties(writer, policy);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static RuntimeResourcePolicy ReadPolicy(JsonElement element, string path)
    {
        RequireObjectWithProperties(
            element,
            path,
            "rpcValueBytes",
            "valueDepth",
            "valueNodes",
            "objectMembers",
            "inlineDecodedBytes",
            "contentReferenceBytes");
        return new RuntimeResourcePolicy(
            ReadInt32(element, "rpcValueBytes", path + ".rpcValueBytes"),
            ReadInt32(element, "valueDepth", path + ".valueDepth"),
            ReadInt32(element, "valueNodes", path + ".valueNodes"),
            ReadInt32(element, "objectMembers", path + ".objectMembers"),
            ReadInt32(element, "inlineDecodedBytes", path + ".inlineDecodedBytes"),
            ReadInt32(element, "contentReferenceBytes", path + ".contentReferenceBytes"));
    }

    private static int ReadInt32(JsonElement parent, string propertyName, string path)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"{path} must be a 32-bit integer.");
        }
        return value;
    }

    private static void RequireObjectWithProperties(
        JsonElement element,
        string path,
        params string[] expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path} must be an object.");
        }

        var expected = expectedProperties.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new InvalidDataException($"{path} contains unsupported property '{property.Name}'.");
            }
            if (!observed.Add(property.Name))
            {
                throw new InvalidDataException($"{path} contains duplicate property '{property.Name}'.");
            }
        }

        var missing = expected.Where(property => !observed.Contains(property)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException($"{path} is missing required property '{missing[0]}'.");
        }
    }

    private static void WritePolicy(Utf8JsonWriter writer, RuntimeResourcePolicy policy)
    {
        writer.WriteStartObject();
        WritePolicyProperties(writer, policy);
        writer.WriteEndObject();
    }

    private static void WritePolicyProperties(Utf8JsonWriter writer, RuntimeResourcePolicy policy)
    {
        writer.WriteNumber("rpcValueBytes", policy.MaximumRpcValueBytes);
        writer.WriteNumber("valueDepth", policy.MaximumValueDepth);
        writer.WriteNumber("valueNodes", policy.MaximumValueNodes);
        writer.WriteNumber("objectMembers", policy.MaximumObjectMembers);
        writer.WriteNumber("inlineDecodedBytes", policy.MaximumInlineDecodedBytes);
        writer.WriteNumber("contentReferenceBytes", policy.MaximumContentReferenceBytes);
    }

    private static string GetPresetName(RuntimeResourcePolicyPreset preset)
        => preset switch
        {
            RuntimeResourcePolicyPreset.Standard => "standard",
            RuntimeResourcePolicyPreset.Large => "large",
            RuntimeResourcePolicyPreset.Custom => "custom",
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
}
